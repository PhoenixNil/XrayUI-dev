namespace XrayUI.Services
{
    /// <summary>
    /// Length of the leading "yyyy/MM/dd HH:mm:ss[.ffffff]" timestamp on a raw
    /// xray log line, or 0 if the line doesn't start with one. Pure string
    /// logic (no WinUI types) so it stays source-linkable into the test
    /// project; the log window uses this to dim the timestamp prefix.
    /// </summary>
    public static class LogLineParser
    {
        public static int TimestampLength(string line)
        {
            if (line.Length < 19 ||
                !char.IsAsciiDigit(line[0]) ||
                line[4] != '/' || line[7] != '/' || line[10] != ' ' ||
                line[13] != ':' || line[16] != ':')
            {
                return 0;
            }

            var timeEnd = line.IndexOf(' ', 11);
            return timeEnd < 0 ? line.Length : timeEnd;
        }
    }
}
