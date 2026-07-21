using XrayUI.Services;

namespace XrayUI.Tests
{
    public class LogLineParserTests
    {
        [Fact]
        public void TimestampedLine_ReturnsPrefixLength()
        {
            const string timestamp = "2026/07/21 16:55:06.567737";
            const string rest = " from 127.0.0.1:50262 accepted //graph.microsoft.com:443 [mixed-in -> proxy]";
            var line = timestamp + rest;

            var length = LogLineParser.TimestampLength(line);

            Assert.Equal(timestamp.Length, length);
            Assert.Equal(timestamp, line[..length]);
            Assert.Equal(rest, line[length..]);
        }

        [Fact]
        public void Ipv6Source_NotMistakenForMissingTimestamp()
        {
            const string timestamp = "2026/07/21 16:54:50.770602";
            var line = timestamp + " from [::1]:5000 accepted //example.com:443 [mixed-in -> direct]";

            Assert.Equal(timestamp.Length, LogLineParser.TimestampLength(line));
        }

        [Fact]
        public void NoTimestamp_ReturnsZero()
        {
            Assert.Equal(0, LogLineParser.TimestampLength("Xray 25.1.30 (Xray, Penetrates Everything.)"));
        }

        [Fact]
        public void TimestampOnly_ReturnsFullLength()
        {
            const string line = "2026/07/21 16:54:50.770602";
            Assert.Equal(line.Length, LogLineParser.TimestampLength(line));
        }

        [Fact]
        public void EmptyLine_ReturnsZero()
        {
            Assert.Equal(0, LogLineParser.TimestampLength(""));
        }
    }
}
