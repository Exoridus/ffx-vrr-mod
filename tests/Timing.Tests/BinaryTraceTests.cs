namespace Fahrenheit.Mods.Vrr;

using System.IO.Compression;
using System.Text;
using Xunit;

public sealed class BinaryTraceTests
{
    [Fact]
    public void Capture_preserves_float_bits_and_writes_independent_recoverable_chunks()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ffxt");
        try
        {
            uint[] words = [0x7fc01234, 0x80000000, 0x3f800000, 0xffffffff];
            using (var trace = new BinaryTraceSession(path, 65536, 64, "{\"schema_version\":1}"))
            {
                Assert.True(trace.TryWrite(2, 19, 7, words));
                trace.Dispose();
                Assert.False(trace.TryWrite(2, 20, 7, words));
            }
            using var reader = new BinaryReader(File.OpenRead(path));
            Assert.Equal("FFXTRACE", Encoding.ASCII.GetString(reader.ReadBytes(8)));
            Assert.Equal(1, reader.ReadInt32());
            reader.ReadBytes(reader.ReadInt32());
            int compressed = reader.ReadInt32(), raw = reader.ReadInt32();
            using var gzip = new GZipStream(new MemoryStream(reader.ReadBytes(compressed)), CompressionMode.Decompress);
            using var decoded = new MemoryStream();
            gzip.CopyTo(decoded);
            Assert.Equal(raw, decoded.Length);
            decoded.Position = 0;
            using var record = new BinaryReader(decoded);
            Assert.Equal(1, record.ReadInt64());
            Assert.True(record.ReadInt64() > 0);
            Assert.Equal(19, record.ReadInt64());
            Assert.Equal(7, record.ReadInt64());
            Assert.True(record.ReadInt32() > 0);
            Assert.Equal(2, record.ReadUInt16());
            Assert.Equal(4, record.ReadUInt16());
            Assert.Equal(words, Enumerable.Range(0, 4).Select(_ => record.ReadUInt32()));
            Assert.Equal(0, reader.ReadInt32());
            Assert.Equal(0, reader.ReadInt32());
            Assert.Equal(1, reader.ReadInt64());
            Assert.Equal(0, reader.ReadInt64());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Bounded_capture_reports_loss_and_never_exceeds_budget()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ffxt");
        try
        {
            using var trace = new BinaryTraceSession(path, 8192, 8, "{}");
            uint[] words = new uint[128];
            Parallel.For(0, 10000, i => { words[0] = (uint)i; trace.TryWrite(1, i, 0, words); });
            trace.Dispose();
            Assert.True(new FileInfo(path).Length <= 8192);
            Assert.True(trace.Dropped > 0);
        }
        finally { File.Delete(path); }
    }
}
