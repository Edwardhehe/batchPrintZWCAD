using System;
using System.Globalization;

namespace PiaNO;

public class PiaHeader
{
    private readonly string _headerData;

    public double PiaFileVersion { get; }
    public short TypeVersion { get; }
    public EnumDecompressionType PiaType { get; }

    public PiaHeader(string headerString)
    {
        _headerData = headerString;

        var headerArray = headerString.Split(new char[] { ',', '_' });
        if (headerArray.Length < 4)
            throw new ArgumentOutOfRangeException();

        var nfi = new NumberFormatInfo
        {
            CurrencyDecimalSeparator = "."
        };
        PiaFileVersion = double.Parse(headerArray[1], nfi);

        var typeString = headerArray[2].Substring(0, 3);
        PiaType = (EnumDecompressionType)Enum.Parse(typeof(EnumDecompressionType), typeString);

        var versionString = headerArray[2].Substring(3).ToUpper().Replace("VER", string.Empty);
        TypeVersion = short.Parse(versionString);
    }

    public override string ToString() => _headerData;
}
