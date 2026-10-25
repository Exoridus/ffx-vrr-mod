using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class FmvLifecycleTests
{
    [Fact]
    public void MissingLibraryMovieKeepsOriginalAndInvalidCandidateCanBeCorrectedWithoutRestart()
    {
        string directory = Path.Combine(Path.GetTempPath(), "fhvrr-library-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            _module._overrides.Clear();
            Set("_fmv_video_directory", directory);
            Set("_fmvAudioHooksReady", true);
            Set("_av1Native", RuntimeHelpers.GetUninitializedObject(typeof(Av1Native)));
            Call("h_fmv_initialize", _manager, 7, (byte)0, (byte)1);
            Assert.Equal("retail", Field<string>("_fmv_last_outcome"));
            string movie = Path.Combine(directory, "opk.webm");
            File.WriteAllText(movie, "invalid");
            Call("h_fmv_initialize", _manager, 7, (byte)0, (byte)1);
            Assert.Equal("override rejected; retail", Field<string>("_fmv_last_outcome"));
            File.WriteAllBytes(movie, FmvVideoLibraryTests.Video());
            Call("h_fmv_initialize", _manager, 7, (byte)0, (byte)1);
            Assert.Equal("replacement selected; decode unverified", Field<string>("_fmv_last_outcome"));
            File.Delete(movie);
            NativeCalls.PlayMovieProbe = () => Assert.Equal(Original, Marshal.PtrToStringAnsi(_manager + EngineAddresses.FmvPathOffset));
            Call("h_fmv_play_start", _manager, 0);
            Assert.Equal("optional movie disappeared; retail", Field<string>("_fmv_last_outcome"));
        }
        finally
        {
            NativeCalls.PlayMovieProbe = null;
            Call("release_fmv_surfaces");
            Directory.Delete(directory, true);
        }
        void Set(string name, object value) => typeof(VrrModule).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_module, value);
    }
}