using System.IO;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace EQ2Lexicon.ACTPlugin.Tests
{
    /// <summary>
    /// v0.1.16 gzip upload: the body is compressed but the HMAC contract is
    /// unchanged — signatures are computed over the UNCOMPRESSED JSON and the
    /// server inflates before validating. These tests pin the roundtrip and
    /// that signing is independent of compression.
    /// </summary>
    public class UploadClientCompressionTests
    {
        private static string Decompress(byte[] gz)
        {
            using (var input = new MemoryStream(gz))
            using (var stream = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                stream.CopyTo(output);
                return Encoding.UTF8.GetString(output.ToArray());
            }
        }

        [Fact]
        public void Compress_Roundtrips_Utf8Json()
        {
            var json = "{\"logger_name\":\"Nosnekr\",\"encounter\":{\"title\":\"a servant of growth\"}}";
            var gz = UploadClient.CompressUtf8Gzip(json);
            Assert.Equal(json, Decompress(gz));
        }

        [Fact]
        public void Compress_Shrinks_Repetitive_ActShapedJson()
        {
            // ACT payloads are dominated by repeated damage-type rows; a
            // realistic body must compress dramatically (the whole point).
            var row = "{\"damage\":12345,\"encdps\":678.9,\"hits\":42,\"crithits\":7,\"blocked\":0,\"misses\":1},";
            var json = "{\"combatants\":[" + string.Concat(System.Linq.Enumerable.Repeat(row, 2000)).TrimEnd(',') + "]}";
            var gz = UploadClient.CompressUtf8Gzip(json);
            Assert.True(gz.Length * 10 < Encoding.UTF8.GetByteCount(json),
                $"expected >10x compression, got {Encoding.UTF8.GetByteCount(json)} -> {gz.Length}");
        }

        [Fact]
        public void Signature_Is_Over_Uncompressed_Json()
        {
            var json = "{\"a\":1}";
            var sigPlain = PayloadSigner.Sign(json, "eq2c_token");
            // Compressing must not change what gets signed — the signature is
            // a function of the JSON alone.
            UploadClient.CompressUtf8Gzip(json);
            Assert.Equal(sigPlain, PayloadSigner.Sign(json, "eq2c_token"));
        }
    }
}
