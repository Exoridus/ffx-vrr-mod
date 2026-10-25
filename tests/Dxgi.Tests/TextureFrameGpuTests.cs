using Fahrenheit.Mods.Vrr;
using TerraFX.Interop.DirectX;
using static TerraFX.Interop.DirectX.DirectX;
using Xunit;

[System.Runtime.Versioning.SupportedOSPlatform("windows6.1")]
public unsafe sealed class TextureFrameGpuTests
{
    [Fact]
    public void BlendsActualBc3FramesAcrossWrapAndEveryMipAndRestoresBindings()
    {
        ID3D11Device* device = null;
        ID3D11DeviceContext* context = null;
        Assert.True(D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP, default, 0,
            null, 0, 7, &device, null, &context) >= 0);
        nint[] sources = new nint[8];
        ID3D11Texture2D* staging = null;
        ID3D11ShaderResourceView* sentinel = null;
        ID3D11Buffer* constant = null;
        ID3D11Buffer* counterBuffer = null;
        ID3D11Buffer* counterReadback = null;
        ID3D11UnorderedAccessView* counterView = null;
        try
        {
            for (int frame = 0; frame < sources.Length; frame++) sources[frame] = (nint)CreateBc3(device, frame % 2 == 0);
            nint deviceAddress = (nint)device, contextAddress = (nint)context;
            nint[] missing = (nint[])sources.Clone();
            missing[3] = 0;
            Assert.Throws<InvalidDataException>(() => { using var rejected = new TextureFrameGpuBlender((ID3D11Device*)deviceAddress, (ID3D11DeviceContext*)contextAddress, missing); });
            nint[] mismatched = (nint[])sources.Clone();
            mismatched[7] = (nint)CreateBc3(device, false, 1);
            try { Assert.Throws<InvalidDataException>(() => { using var rejected = new TextureFrameGpuBlender((ID3D11Device*)deviceAddress, (ID3D11DeviceContext*)contextAddress, mismatched); }); }
            finally { ((ID3D11Resource*)mismatched[7])->Release(); }
            nint[] srgb = (nint[])sources.Clone();
            srgb[0] = (nint)CreateBc3(device, true, 10, DXGI_FORMAT.DXGI_FORMAT_BC3_UNORM_SRGB);
            try { Assert.Throws<InvalidDataException>(() => { using var rejected = new TextureFrameGpuBlender((ID3D11Device*)deviceAddress, (ID3D11DeviceContext*)contextAddress, srgb); }); }
            finally { ((ID3D11Resource*)srgb[0])->Release(); }
            using var blender = new TextureFrameGpuBlender(device, context, sources);
            ID3D11Resource* source = (ID3D11Resource*)sources[0];
            Assert.True(device->CreateShaderResourceView(source, null, &sentinel) >= 0);
            context->CSSetShaderResources(0, 1, &sentinel);
            var constantDescription = new D3D11_BUFFER_DESC { ByteWidth = 16, BindFlags = 4 };
            Assert.True(device->CreateBuffer(&constantDescription, null, &constant) >= 0);
            context->CSSetConstantBuffers(0, 1, &constant);
            var counterDescription = new D3D11_BUFFER_DESC { ByteWidth = 16, BindFlags = 0x80,
                MiscFlags = 0x40, StructureByteStride = 4 };
            Assert.True(device->CreateBuffer(&counterDescription, null, &counterBuffer) >= 0);
            var counterViewDescription = new D3D11_UNORDERED_ACCESS_VIEW_DESC {
                ViewDimension = D3D11_UAV_DIMENSION.D3D11_UAV_DIMENSION_BUFFER };
            counterViewDescription.Buffer.NumElements = 4;
            counterViewDescription.Buffer.Flags = 4;
            Assert.True(device->CreateUnorderedAccessView((ID3D11Resource*)counterBuffer, &counterViewDescription, &counterView) >= 0);
            uint counterValue = 3;
            context->CSSetUnorderedAccessViews(0, 1, &counterView, &counterValue);
            Assert.NotEqual(0, (nint)blender.Blend(7, 0, .5f));
            ID3D11ShaderResourceView* restored = null;
            context->CSGetShaderResources(0, 1, &restored);
            Assert.Equal((nint)sentinel, (nint)restored);
            if (restored != null) restored->Release();
            ID3D11Buffer* restoredConstant = null;
            context->CSGetConstantBuffers(0, 1, &restoredConstant);
            Assert.Equal((nint)constant, (nint)restoredConstant);
            if (restoredConstant != null) restoredConstant->Release();
            ID3D11UnorderedAccessView* restoredCounter = null;
            context->CSGetUnorderedAccessViews(0, 1, &restoredCounter);
            Assert.Equal((nint)counterView, (nint)restoredCounter);
            if (restoredCounter != null) restoredCounter->Release();
            var readbackDescription = new D3D11_BUFFER_DESC { ByteWidth = 16, Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
                CPUAccessFlags = 0x20000 };
            Assert.True(device->CreateBuffer(&readbackDescription, null, &counterReadback) >= 0);
            context->CopyStructureCount(counterReadback, 0, counterView);
            D3D11_MAPPED_SUBRESOURCE mappedCounter;
            Assert.True(context->Map((ID3D11Resource*)counterReadback, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mappedCounter) >= 0);
            try { Assert.Equal(3u, *(uint*)mappedCounter.pData); }
            finally { context->Unmap((ID3D11Resource*)counterReadback, 0); }
            ID3D11ComputeShader* restoredShader = null;
            uint classCount = 0;
            context->CSGetShader(&restoredShader, null, &classCount);
            Assert.Equal(0, (nint)restoredShader);
            Assert.Equal(0u, classCount);
            D3D11_TEXTURE2D_DESC description;
            blender.Output->GetDesc(&description);
            Assert.Equal(10u, description.MipLevels);
            Assert.Equal(DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, description.Format);
            description.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
            description.BindFlags = 0;
            description.CPUAccessFlags = 0x20000;
            Assert.True(device->CreateTexture2D(&description, null, &staging) >= 0);
            CheckPixels(context, blender.Output, staging, 128, 0, 128, 128);
            blender.Blend(0, 1, 0);
            CheckPixels(context, blender.Output, staging, 255, 0, 0, 64);
            blender.Blend(7, 0, 1);
            CheckPixels(context, blender.Output, staging, 255, 0, 0, 64);
            Assert.Throws<ArgumentOutOfRangeException>(() => blender.Blend(8, 0, .5f));
            Assert.Throws<ArgumentOutOfRangeException>(() => blender.Blend(0, 1, float.NaN));
            blender.Dispose();
            Assert.Equal(0, (nint)blender.Output);
        }
        finally
        {
            context->ClearState();
            if (sentinel != null) sentinel->Release();
            if (constant != null) constant->Release();
            if (counterView != null) counterView->Release();
            if (counterBuffer != null) counterBuffer->Release();
            if (counterReadback != null) counterReadback->Release();
            if (staging != null) staging->Release();
            foreach (nint source in sources) if (source != 0) ((ID3D11Resource*)source)->Release();
            context->Release();
            device->Release();
        }
    }

    private static ID3D11Texture2D* CreateBc3(ID3D11Device* device, bool red, uint mips = 10,
        DXGI_FORMAT format = DXGI_FORMAT.DXGI_FORMAT_BC3_UNORM)
    {
        var description = new D3D11_TEXTURE2D_DESC { Width = 512, Height = 128, MipLevels = mips, ArraySize = 1,
            Format = format, BindFlags = 8,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 } };
        D3D11_SUBRESOURCE_DATA* data = stackalloc D3D11_SUBRESOURCE_DATA[10];
        nint[] blocks = new nint[10];
        try
        {
            for (int mip = 0; mip < 10; mip++)
            {
                int columns = Math.Max(1, (Math.Max(1, 512 >> mip) + 3) / 4);
                int rows = Math.Max(1, (Math.Max(1, 128 >> mip) + 3) / 4);
                byte* bytes = (byte*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)(columns * rows * 16));
                blocks[mip] = (nint)bytes;
                for (int block = 0; block < columns * rows; block++)
                {
                    bytes[block * 16] = bytes[block * 16 + 1] = red ? (byte)(64 + mip) : (byte)(192 - mip);
                    *(ushort*)(bytes + block * 16 + 8) = *(ushort*)(bytes + block * 16 + 10) = red ? (ushort)((31 - mip) << 11) : (ushort)(31 - mip);
                }
                data[mip] = new D3D11_SUBRESOURCE_DATA { pSysMem = bytes, SysMemPitch = (uint)(columns * 16) };
            }
            ID3D11Texture2D* texture = null;
            Assert.True(device->CreateTexture2D(&description, data, &texture) >= 0);
            return texture;
        }
        finally { foreach (nint block in blocks) System.Runtime.InteropServices.NativeMemory.Free((void*)block); }
    }

    private static void CheckPixels(ID3D11DeviceContext* context, ID3D11Texture2D* output,
        ID3D11Texture2D* staging, int red, int green, int blue, int alpha)
    {
        context->CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)output);
        for (uint mip = 0; mip < 10; mip++)
        {
            D3D11_MAPPED_SUBRESOURCE mapped;
            Assert.True(context->Map((ID3D11Resource*)staging, mip, D3D11_MAP.D3D11_MAP_READ, 0, &mapped) >= 0);
            try
            {
                int fiveBit = 31 - (int)mip;
                int level = (fiveBit << 3) | (fiveBit >> 2);
                int expectedRed = (int)MathF.Round(red * level / 255f);
                int expectedBlue = (int)MathF.Round(blue * level / 255f);
                int expectedAlpha = alpha == 64 ? alpha + (int)mip : alpha;
                byte* pixel = (byte*)mapped.pData;
                Assert.InRange((int)pixel[0], Math.Max(0, expectedRed - 1), Math.Min(255, expectedRed + 1));
                Assert.Equal(green, pixel[1]);
                Assert.InRange((int)pixel[2], Math.Max(0, expectedBlue - 1), Math.Min(255, expectedBlue + 1));
                Assert.Equal(expectedAlpha, pixel[3]);
            }
            finally { context->Unmap((ID3D11Resource*)staging, mip); }
        }
    }
}
