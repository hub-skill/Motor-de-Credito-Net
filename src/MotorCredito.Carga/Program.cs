using System.Text;
using MotorCredito.Carga;
using MotorCredito.Core.Cgu;
using MotorCredito.Core.Configuracao;

// Ferramenta do Motor de Crédito .NET. Uso (na raiz do repositório):
//   dotnet run --project src/MotorCredito.Carga -- <comando> [--banco postgres|oracle|todos] [...]
Console.OutputEncoding = Encoding.UTF8;
ArquivoEnv.CarregarSeExistir();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, evento) =>
{
    evento.Cancel = true;
    cts.Cancel();
};

var comando = args.Length > 0 ? args[0] : "ajuda";
var opcoes = Opcoes.Ler(args.Skip(1));
try
{
    return comando switch
    {
        "migrar" => await Comandos.MigrarAsync(opcoes, cts.Token),
        "carregar" => await Comandos.CarregarAsync(opcoes, cts.Token),
        "comparar" => await Comandos.CompararAsync(opcoes, cts.Token),
        "testar-api" => await Comandos.TestarApiAsync(opcoes, cts.Token),
        _ => Comandos.Ajuda(),
    };
}
catch (ArquivoCguInvalidoException erro)
{
    Console.WriteLine($"[erro] {erro.Message} Primeiros problemas:");
    foreach (var problema in erro.Problemas.Take(10))
    {
        Console.WriteLine($"   {problema}");
    }

    return 2;
}
