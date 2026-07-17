using XrayUI.Services;

namespace XrayUI.Tests
{
    public class CustomRuleValueParserTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   \r\n  \n ")]
        public void Parse_EmptyInput_ReturnsEmptyList(string? text)
        {
            Assert.Empty(CustomRuleValueParser.Parse(text));
        }

        [Fact]
        public void Parse_SplitsOnLineBreaksOnly_CommasStayInsideValues()
        {
            var values = CustomRuleValueParser.Parse("domain:a.com,b.com\ngeosite:cn");

            Assert.Equal(["domain:a.com,b.com", "geosite:cn"], values);
        }

        [Fact]
        public void Parse_TrimsWhitespaceAndSkipsBlankLines()
        {
            var values = CustomRuleValueParser.Parse("  a.com  \r\n\r\n\tb.com\t\n");

            Assert.Equal(["a.com", "b.com"], values);
        }

        [Fact]
        public void Parse_DeduplicatesCaseInsensitively_KeepsFirstCasing()
        {
            var values = CustomRuleValueParser.Parse("Example.com\nexample.com\nEXAMPLE.COM\nother.net");

            Assert.Equal(["Example.com", "other.net"], values);
        }

        [Fact]
        public void Parse_PreservesRegexpAndWindowsPathCharacters()
        {
            var values = CustomRuleValueParser.Parse("regexp:\\.goo.*\\.com$;keep\nC:\\Program Files\\app.exe");

            Assert.Equal(2, values.Count);
            Assert.Equal(@"regexp:\.goo.*\.com$;keep", values[0]);
            Assert.Equal(@"C:\Program Files\app.exe", values[1]);
        }
    }
}
