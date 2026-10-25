using System.Runtime.InteropServices;
using System.Text;
using TerraFX.Interop.DirectX;
using static TerraFX.Interop.DirectX.DirectX;

namespace Fahrenheit.Mods.Vrr;

[System.Runtime.Versioning.SupportedOSPlatform("windows6.1")]
internal unsafe sealed class TextureFrameGpuBlender : IDisposable
{
    private ID3D11Device* _device;
    private ID3D11DeviceContext* _context;
    private ID3D11Texture2D* _output;
    private ID3D11ShaderResourceView* _outputView;
    private ID3D11ComputeShader* _shader;
    private ID3D11Buffer* _parameters;
    private readonly nint[] _sources = new nint[8];
    private nint[] _targets = [];
    private uint _width, _height;
    private uint _lastFirst = uint.MaxValue, _lastNext, _lastAlpha;
    internal ID3D11Texture2D* Output => _output;

    internal TextureFrameGpuBlender(ID3D11Device* device, ID3D11DeviceContext* context, ReadOnlySpan<nint> resources)
    {
        if (device == null || context == null || resources.Length != 8)
            throw new ArgumentException("Texture interpolation requires one device/context and eight source frames.");
        _device = device;
        _context = context;
        device->AddRef();
        context->AddRef();
        try
        {
            D3D11_TEXTURE2D_DESC expected = default;
            for (int i = 0; i < resources.Length; i++)
            {
                if (resources[i] == 0) throw new InvalidDataException("Animation has a missing texture frame.");
                ID3D11Texture2D* texture = null;
                Guid iid = typeof(ID3D11Texture2D).GUID;
                Marshal.ThrowExceptionForHR((int)((ID3D11Resource*)resources[i])->QueryInterface(&iid, (void**)&texture));
                D3D11_TEXTURE2D_DESC description;
                try { texture->GetDesc(&description); }
                finally { texture->Release(); }
                if (description.Width == 0 || description.Height == 0 || description.MipLevels == 0
                    || description.ArraySize != 1 || description.SampleDesc.Count != 1
                    || description.Format is not (DXGI_FORMAT.DXGI_FORMAT_BC3_UNORM or DXGI_FORMAT.DXGI_FORMAT_BC3_TYPELESS))
                    throw new InvalidDataException("Animation interpolation requires single-sample BC3_UNORM Texture2D frames.");
                if (i == 0) expected = description;
                else if (description.Width != expected.Width || description.Height != expected.Height || description.MipLevels != expected.MipLevels)
                    throw new InvalidDataException("Animation frame sizes and mip counts differ.");
                var view = new D3D11_SHADER_RESOURCE_VIEW_DESC { Format = DXGI_FORMAT.DXGI_FORMAT_BC3_UNORM,
                    ViewDimension = D3D_SRV_DIMENSION.D3D_SRV_DIMENSION_TEXTURE2D };
                view.Texture2D.MipLevels = description.MipLevels;
                ID3D11ShaderResourceView* input = null;
                Marshal.ThrowExceptionForHR((int)device->CreateShaderResourceView((ID3D11Resource*)resources[i], &view, &input));
                _sources[i] = (nint)input;
            }
            _width = expected.Width;
            _height = expected.Height;
            expected.Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM;
            expected.Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT;
            expected.BindFlags = 8 | 0x80;
            expected.CPUAccessFlags = expected.MiscFlags = 0;
            fixed (ID3D11Texture2D** output = &_output)
                Marshal.ThrowExceptionForHR((int)device->CreateTexture2D(&expected, null, output));
            fixed (ID3D11ShaderResourceView** outputView = &_outputView)
                Marshal.ThrowExceptionForHR((int)device->CreateShaderResourceView((ID3D11Resource*)_output, null, outputView));
            _targets = new nint[expected.MipLevels];
            for (uint mip = 0; mip < expected.MipLevels; mip++)
            {
                var view = new D3D11_UNORDERED_ACCESS_VIEW_DESC { Format = expected.Format,
                    ViewDimension = D3D11_UAV_DIMENSION.D3D11_UAV_DIMENSION_TEXTURE2D };
                view.Texture2D.MipSlice = mip;
                ID3D11UnorderedAccessView* target = null;
                Marshal.ThrowExceptionForHR((int)device->CreateUnorderedAccessView((ID3D11Resource*)_output, &view, &target));
                _targets[mip] = (nint)target;
            }
            var buffer = new D3D11_BUFFER_DESC { ByteWidth = 16, BindFlags = 4 };
            fixed (ID3D11Buffer** parameters = &_parameters)
                Marshal.ThrowExceptionForHR((int)device->CreateBuffer(&buffer, null, parameters));
            compile_shader();
        }
        catch { Dispose(); throw; }
    }

    private void compile_shader()
    {
        const string source = "Texture2D<float4> First:register(t0); Texture2D<float4> Next:register(t1);"
            + "RWTexture2D<float4> Output:register(u0); cbuffer Parameters:register(b0){uint Mip;float Alpha;uint2 Size;}"
            + "[numthreads(8,8,1)] void main(uint3 p:SV_DispatchThreadID){if(any(p.xy>=Size))return;"
            + "Output[p.xy]=lerp(First.Load(int3(p.xy,Mip)),Next.Load(int3(p.xy,Mip)),Alpha);}";
        byte[] code = Encoding.ASCII.GetBytes(source), entry = "main\0"u8.ToArray(), profile = "cs_5_0\0"u8.ToArray();
        ID3DBlob* compiled = null;
        ID3DBlob* errors = null;
        try
        {
            fixed (byte* bytes = code)
            fixed (byte* name = entry)
            fixed (byte* target = profile)
            {
                int result = (int)D3DCompile(bytes, (nuint)code.Length, null, null, null,
                    (sbyte*)name, (sbyte*)target, 0, 0, &compiled, &errors);
                if (result < 0) throw new InvalidOperationException(errors == null ? $"Texture blend shader failed: {result:X8}."
                    : Marshal.PtrToStringAnsi((nint)errors->GetBufferPointer(), checked((int)errors->GetBufferSize())));
            }
            fixed (ID3D11ComputeShader** shader = &_shader)
                Marshal.ThrowExceptionForHR((int)_device->CreateComputeShader(compiled->GetBufferPointer(), compiled->GetBufferSize(), null, shader));
        }
        finally
        {
            if (compiled != null) compiled->Release();
            if (errors != null) errors->Release();
        }
    }

    internal ID3D11ShaderResourceView* Blend(uint first, uint next, float alpha)
    {
        ObjectDisposedException.ThrowIf(_device == null, this);
        if (first >= 8 || next >= 8 || !float.IsFinite(alpha) || alpha < 0 || alpha > 1)
            throw new ArgumentOutOfRangeException(nameof(first));
        uint alphaBits = BitConverter.SingleToUInt32Bits(alpha);
        if (_lastFirst == first && _lastNext == next && _lastAlpha == alphaBits) return _outputView;
        ID3D11ComputeShader* shader = null;
        ID3D11ClassInstance** instances = stackalloc ID3D11ClassInstance*[256];
        new Span<nint>(instances, 256).Clear();
        uint instanceCount = 256;
        ID3D11ShaderResourceView** savedViews = stackalloc ID3D11ShaderResourceView*[2];
        ID3D11UnorderedAccessView* savedTarget = null;
        ID3D11Buffer* savedBuffer = null;
        _context->CSGetShader(&shader, instances, &instanceCount);
        _context->CSGetShaderResources(0, 2, savedViews);
        _context->CSGetUnorderedAccessViews(0, 1, &savedTarget);
        _context->CSGetConstantBuffers(0, 1, &savedBuffer);
        try
        {
            _context->CSSetShader(_shader, null, 0);
            ID3D11ShaderResourceView** inputs = stackalloc ID3D11ShaderResourceView*[2];
            inputs[0] = (ID3D11ShaderResourceView*)_sources[first];
            inputs[1] = (ID3D11ShaderResourceView*)_sources[next];
            _context->CSSetShaderResources(0, 2, inputs);
            ID3D11Buffer* parameters = _parameters;
            _context->CSSetConstantBuffers(0, 1, &parameters);
            uint* words = stackalloc uint[4];
            words[1] = alphaBits;
            for (int mip = 0; mip < _targets.Length; mip++)
            {
                words[0] = (uint)mip;
                words[2] = Math.Max(1u, _width >> mip);
                words[3] = Math.Max(1u, _height >> mip);
                _context->UpdateSubresource((ID3D11Resource*)_parameters, 0, null, words, 0, 0);
                ID3D11UnorderedAccessView* output = (ID3D11UnorderedAccessView*)_targets[mip];
                _context->CSSetUnorderedAccessViews(0, 1, &output, null);
                _context->Dispatch((words[2] + 7) / 8, (words[3] + 7) / 8, 1);
            }
            Marshal.ThrowExceptionForHR((int)_device->GetDeviceRemovedReason());
            _lastFirst = first;
            _lastNext = next;
            _lastAlpha = alphaBits;
        }
        finally
        {
            ID3D11UnorderedAccessView* empty = null;
            _context->CSSetUnorderedAccessViews(0, 1, &empty, null);
            _context->CSSetShaderResources(0, 2, savedViews);
            // Null initial counts preserve any counter belonging to the restored native UAV.
            _context->CSSetUnorderedAccessViews(0, 1, &savedTarget, null);
            _context->CSSetConstantBuffers(0, 1, &savedBuffer);
            _context->CSSetShader(shader, instances, instanceCount);
            if (shader != null) shader->Release();
            for (uint i = 0; i < instanceCount; i++) if (instances[i] != null) instances[i]->Release();
            for (int i = 0; i < 2; i++) if (savedViews[i] != null) savedViews[i]->Release();
            if (savedTarget != null) savedTarget->Release();
            if (savedBuffer != null) savedBuffer->Release();
        }
        return _outputView;
    }

    public void Dispose()
    {
        if (_outputView != null) { _outputView->Release(); _outputView = null; }
        foreach (nint target in _targets) if (target != 0) ((ID3D11UnorderedAccessView*)target)->Release();
        Array.Clear(_targets);
        foreach (nint source in _sources) if (source != 0) ((ID3D11ShaderResourceView*)source)->Release();
        Array.Clear(_sources);
        if (_output != null) { _output->Release(); _output = null; }
        if (_parameters != null) { _parameters->Release(); _parameters = null; }
        if (_shader != null) { _shader->Release(); _shader = null; }
        if (_context != null) { _context->Release(); _context = null; }
        if (_device != null) { _device->Release(); _device = null; }
    }
}
