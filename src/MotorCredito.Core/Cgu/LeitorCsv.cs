using System.Text;

namespace MotorCredito.Core.Cgu;

/// <summary>
/// Leitor de CSV com campos entre aspas, aspas dobradas e quebras de linha dentro do campo.
/// Linhas totalmente vazias são ignoradas.
/// </summary>
public static class LeitorCsv
{
    private enum Estado
    {
        InicioRegistro,
        ComeuCr,
        InicioCampo,
        EmCampo,
        EmAspas,
        AspaEmAspas,
    }

    public static IEnumerable<IReadOnlyList<string>> Ler(TextReader leitor, char delimitador = ';')
    {
        ArgumentNullException.ThrowIfNull(leitor);
        var campos = new List<string>();
        var campo = new StringBuilder();
        var estado = Estado.InicioRegistro;

        int lido;
        while ((lido = leitor.Read()) >= 0)
        {
            var c = (char)lido;
            var fimDeLinha = c is '\r' or '\n';

            if (estado == Estado.ComeuCr)
            {
                estado = Estado.InicioRegistro;
                if (c == '\n')
                {
                    continue; // \r\n conta como uma quebra só
                }
            }

            if (estado == Estado.InicioRegistro)
            {
                if (fimDeLinha)
                {
                    estado = c == '\r' ? Estado.ComeuCr : Estado.InicioRegistro;
                    continue; // linha vazia
                }

                estado = Estado.InicioCampo;
            }

            var fechaCampo = false;
            var fechaRegistro = false;
            switch (estado)
            {
                case Estado.InicioCampo:
                    if (c == '"')
                    {
                        estado = Estado.EmAspas;
                    }
                    else if (c == delimitador)
                    {
                        fechaCampo = true;
                    }
                    else if (fimDeLinha)
                    {
                        fechaRegistro = true;
                    }
                    else
                    {
                        campo.Append(c);
                        estado = Estado.EmCampo;
                    }

                    break;

                case Estado.EmCampo:
                    if (c == delimitador)
                    {
                        fechaCampo = true;
                    }
                    else if (fimDeLinha)
                    {
                        fechaRegistro = true;
                    }
                    else
                    {
                        campo.Append(c);
                    }

                    break;

                case Estado.EmAspas:
                    if (c == '"')
                    {
                        estado = Estado.AspaEmAspas;
                    }
                    else
                    {
                        campo.Append(c); // inclui quebras de linha dentro do campo
                    }

                    break;

                case Estado.AspaEmAspas:
                    if (c == '"')
                    {
                        campo.Append('"'); // aspas dobradas = uma aspa literal
                        estado = Estado.EmAspas;
                    }
                    else if (c == delimitador)
                    {
                        fechaCampo = true;
                    }
                    else if (fimDeLinha)
                    {
                        fechaRegistro = true;
                    }
                    else
                    {
                        campo.Append(c);
                        estado = Estado.EmCampo;
                    }

                    break;
            }

            if (fechaCampo || fechaRegistro)
            {
                campos.Add(campo.ToString());
                campo.Clear();
                estado = Estado.InicioCampo;
            }

            if (fechaRegistro)
            {
                yield return campos.ToArray();
                campos.Clear();
                estado = c == '\r' ? Estado.ComeuCr : Estado.InicioRegistro;
            }
        }

        // Último registro sem quebra de linha no fim do arquivo.
        if (estado is not (Estado.InicioRegistro or Estado.ComeuCr))
        {
            campos.Add(campo.ToString());
            yield return campos.ToArray();
        }
    }
}
