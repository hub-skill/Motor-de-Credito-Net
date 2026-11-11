using System.Globalization;
using System.Text;

namespace MotorCredito.Core.Cgu;

/// <summary>Conversões dos textos dos arquivos da CGU. Lançam FormatException quando o valor é inválido.</summary>
public static class Conversores
{
    private static readonly string[] FormatosDeData = ["dd/MM/yyyy", "d/M/yyyy"];

    public static object? Texto(string valor)
    {
        ArgumentNullException.ThrowIfNull(valor);
        var limpo = valor.Trim();
        return limpo.Length == 0 ? null : limpo;
    }

    /// <summary>dd/mm/aaaa. Vazio e "Não informada" viram nulo.</summary>
    public static object? DataBr(string valor)
    {
        ArgumentNullException.ThrowIfNull(valor);
        var limpo = valor.Trim();
        if (limpo.Length == 0 || limpo.StartsWith("não informad", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return DateOnly.ParseExact(limpo, FormatosDeData, CultureInfo.InvariantCulture, DateTimeStyles.None);
    }

    /// <summary>"813.870,96" ou "813870,96" viram 813870.96.</summary>
    public static object? DecimalBr(string valor)
    {
        ArgumentNullException.ThrowIfNull(valor);
        var limpo = valor.Trim();
        if (limpo.Length == 0)
        {
            return null;
        }

        var normalizado = limpo.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.');
        return decimal.Parse(
            normalizado, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
    }

    public static object? Inteiro(string valor)
    {
        ArgumentNullException.ThrowIfNull(valor);
        return long.Parse(valor.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    }

    /// <summary>"***.123.456-**" vira "123456": os dígitos que a CGU deixa visíveis.</summary>
    public static object? MioloCpf(string valor)
    {
        ArgumentNullException.ThrowIfNull(valor);
        var digitos = new StringBuilder(6);
        foreach (var c in valor)
        {
            if (char.IsDigit(c))
            {
                digitos.Append(c);
            }
        }

        return digitos.ToString();
    }
}
