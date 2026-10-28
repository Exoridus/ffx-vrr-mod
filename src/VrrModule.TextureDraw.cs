using TerraFX.Interop.DirectX;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private readonly object _textureDrawLock = new();
    private readonly TextureDrawHookRegistry<TextureDrawHooks> _textureDrawHooks = new();

    private bool texture_draw_device(ID3D11DeviceContext* context)
    {
        lock (_textureDrawLock)
        {
            nint* table = *(nint**)context;
            foreach (bool indexed in new[] { true, false })
            {
                nint target = table[indexed ? 12 : 13];
                if (!_textureDrawHooks.Install(indexed, target, () => new TextureDrawHooks(this, target, indexed),
                    hook => hook.Install(indexed))) return false;
            }
            return true;
        }
    }

    private sealed class TextureDrawHooks
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void d_indexed(ID3D11DeviceContext* context, uint count, uint start, int baseVertex);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void d_plain(ID3D11DeviceContext* context, uint count, uint start);
        private readonly VrrModule _owner;
        private readonly nint _target;
        private readonly TextureDrawDispatch _dispatch;
        internal TextureDrawHooks(VrrModule owner, nint target, bool indexed)
        {
            _owner = owner;
            _target = target;
            _dispatch = new(indexed, () => Resolve(indexed),
                () => owner._textureFrameBound && owner.texture_frame_blend_available(),
                context => (nint)owner.texture_frame_prepare_draw((ID3D11DeviceContext*)context),
                (context, native) => texture_frame_restore_draw((ID3D11DeviceContext*)context, (ID3D11ShaderResourceView*)native),
                (context, isIndexed, count) => owner.texture_blend_probe_draw((ID3D11DeviceContext*)context, isIndexed, count));
        }
        internal bool Install(bool indexed) => indexed
            ? HookSites.Install(HookSiteId.TextureDrawIndexed, _owner._hookRegistration, _target, _owner._hookRegistrationGeneration, () => new FhCallHandle<d_indexed>(new FhCallLocation(_target)).hook(_owner, _owner.with_hook_continuation<d_indexed>(Indexed)))
            : HookSites.Install(HookSiteId.TextureDrawPlain, _owner._hookRegistration, _target, _owner._hookRegistrationGeneration, () => new FhCallHandle<d_plain>(new FhCallLocation(_target)).hook(_owner, _owner.with_hook_continuation<d_plain>(Plain)));
        private TextureDrawNext Resolve(bool indexed)
        {
            // Fahrenheit appends later hooks by patching the existing trampoline, retaining its address.
            // Resolve after commit; caching that continuation still reaches subsequently appended hooks.
            if (indexed)
            {
                var next = new FhCallHandle<d_indexed>(new FhCallLocation(_target)).chain_from(Indexed).fnptr!;
                return (context, count, start, vertex) => next((ID3D11DeviceContext*)context, count, start, vertex);
            }
            else
            {
                var next = new FhCallHandle<d_plain>(new FhCallLocation(_target)).chain_from(Plain).fnptr!;
                return (context, count, start, _) => next((ID3D11DeviceContext*)context, count, start);
            }
        }
        [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
        private void Indexed(ID3D11DeviceContext* context, uint count, uint start, int baseVertex)
        {
            if (!_owner.RuntimeEffectsArmed)
            {
                HookSites.Native(() => new FhCallHandle<d_indexed>(new FhCallLocation(_target)).chain_from(Indexed).fnptr)(context, count, start, baseVertex);
                return;
            }
            _dispatch.Draw((nint)context, count, start, baseVertex);
        }
        [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
        private void Plain(ID3D11DeviceContext* context, uint count, uint start)
        {
            if (!_owner.RuntimeEffectsArmed)
            {
                HookSites.Native(() => new FhCallHandle<d_plain>(new FhCallLocation(_target)).chain_from(Plain).fnptr)(context, count, start);
                return;
            }
            _dispatch.Draw((nint)context, count, start, 0);
        }
    }
}
