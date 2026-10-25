using System.Runtime.InteropServices;
using Xunit;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class FmvLifecycleTests
{
    [Fact]
    public void AudioFilteringOnlyHidesUnselectedTracksOwnedByTheSelectedSegment()
    {
        nint player = _manager + 0x38;
        nint track = (nint)NativeMemory.AllocZeroed(0x40);
        try
        {
            *(nint*)(player + 0x110) = 0x111000;
            *(nint*)(track + 8) = 0x111000;
            *(int*)(track + 0x24) = 3;
            Call("begin_fmv_audio_selection", player, 2);
            Assert.Equal(0, Call("filter_fmv_audio_type", track, 2));
            Call("end_fmv_audio_selection");
            Assert.Equal(0, Call("filter_fmv_audio_type", track, 2));
            Assert.Equal(1, Call("filter_fmv_audio_type", track, 1));
            *(int*)(track + 0x24) = 2;
            Assert.Equal(2, Call("filter_fmv_audio_type", track, 2));
            *(nint*)(track + 8) = 0x222000;
            Assert.Equal(2, Call("filter_fmv_audio_type", track, 2));
        }
        finally { NativeMemory.Free((void*)track); }
    }

    [Fact]
    public void InitializerStopPreservesPendingLanguageAndFinalStopClearsActiveOwnership()
    {
        nint player = _manager + 0x38;
        nint track = (nint)NativeMemory.AllocZeroed(0x40);
        try
        {
            *(nint*)(player + 0x110) = 0x111000;
            *(nint*)(track + 8) = 0x111000;
            *(int*)(track + 0x24) = 2;
            Call("begin_fmv_audio_selection", player, 3);
            Call("clear_fmv_audio_selection", player);
            Assert.Equal(0, Call("filter_fmv_audio_type", track, 2));
            Call("end_fmv_audio_selection");
            Call("clear_fmv_audio_selection", player + 0x1000);
            Assert.Equal(0, Call("filter_fmv_audio_type", track, 2));
            Call("clear_fmv_audio_selection", player);
            Assert.Equal(2, Call("filter_fmv_audio_type", track, 2));
        }
        finally { NativeMemory.Free((void*)track); }
    }
}
