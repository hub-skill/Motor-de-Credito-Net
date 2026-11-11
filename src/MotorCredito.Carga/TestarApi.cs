using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MotorCredito.Dados;
using Npgsql;

namespace MotorCredito.Carga;

/// <summary>Chama a API em execução como um cliente real e confere cada resposta. Não exibe chave nem nomes.</summary>
internal static partial class Comandos
{
    public static async Task<int> TestarApiAsync(Opcoes opcoes, CancellationToken cancellationToken)
    {
        var url = (opcoes.Valor("url") ?? "http://127.0.0.1:5080").TrimEnd('/');
        var chave = Ambiente.Obter("Api__ApiKey");
        using var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(30) };
        var falhas = 0;

        async Task Conferir(string nome, HttpStatusCode esperado, object? corpo, bool comChave, Func<JsonElement, string?>? checar = null)
        {
            using var requisicao = new HttpRequestMessage(corpo is null ? HttpMethod.Get : HttpMethod.Post,
                corpo is null ? "/health/ready" : "/api/v1/triagem");
            if (corpo is not null)
            {
                requisicao.Content = JsonContent.Create(corpo);
            }

            if (comChave)
            {
                requisicao.Headers.Add("X-API-Key", chave);
            }

            var relogio = Stopwatch.StartNew();
            using var resposta = await http.SendAsync(requisicao, cancellationToken);
            var texto = await resposta.Content.ReadAsStringAsync(cancellationToken);
            var ms = relogio.ElapsedMilliseconds;
            string? problema = resposta.StatusCode != esperado ? $"HTTP {(int)resposta.StatusCode}, esperado {(int)esperado}" : null;
            if (problema is null && checar is not null)
            {
                using var json = JsonDocument.Parse(texto);
                problema = checar(json.RootElement);
            }

            if (problema is null)
            {
                Console.WriteLine($"[ok]    {nome,-46} HTTP {(int)resposta.StatusCode} em {ms,4} ms");
            }
            else
            {
                falhas++;
                Console.WriteLine($"[FALHA] {nome,-46} {problema}");
            }
        }

        static string? Resultado(JsonElement j, string esperado) =>
            j.GetProperty("resultado").GetString() == esperado ? null : $"resultado {j.GetProperty("resultado")}, esperado {esperado}";

        await Conferir("health/ready", HttpStatusCode.OK, null, comChave: false);
        await Conferir("CNPJ sancionado (CEIS) -> bloquear", HttpStatusCode.OK,
            new { documento = "19.182.685/0001-62" }, true, j => Resultado(j, "bloquear"));
        await Conferir("CNPJ sem sanção -> liberar", HttpStatusCode.OK,
            new { documento = "00.000.000/0001-91" }, true, j => Resultado(j, "liberar"));
        await Conferir("CPF sem restrição, com nome -> liberar", HttpStatusCode.OK,
            new { documento = "529.982.247-25", nome = "Fulano de Tal" }, true,
            j => Resultado(j, "liberar") ?? (j.GetProperty("documento").GetString() == "***.982.247-**" ? null : "CPF não mascarado"));
        await Conferir("CPF inválido -> 422", HttpStatusCode.UnprocessableEntity, new { documento = "529.982.247-24" }, true,
            j => j.GetProperty("code").GetString() == "entrada_invalida" ? null : "código de erro inesperado");
        await Conferir("sem chave -> 401", HttpStatusCode.Unauthorized, new { documento = "00000000000191" }, false);

        // PEP real: pega um prefeito em exercício e monta um CPF válido com os 6 dígitos visíveis.
        var bancos = BancosSettings.DoAmbiente();
        await using var con = new NpgsqlConnection(ConexaoDoAppPostgres(bancos, "motor-credito-net-teste-api"));
        await con.OpenAsync(cancellationToken);
        await using var comando = new NpgsqlCommand(
            $"select cpf_miolo, nome from {Esquemas.PostgresNet}.peps"
            + " where descricao_funcao = 'PREFEITO' and data_fim_exercicio >= current_date order by cpf_miolo limit 1", con);
        await using var leitor = await comando.ExecuteReaderAsync(cancellationToken);
        if (await leitor.ReadAsync(cancellationToken))
        {
            var cpf = CpfComMiolo(leitor.GetString(0));
            var nome = leitor.GetString(1);
            await Conferir("PEP real (prefeito), nome em minúsculas -> revisar", HttpStatusCode.OK,
                new { documento = cpf, nome = nome.ToLowerInvariant() }, true,
                j => Resultado(j, "revisar") ?? (j.GetProperty("pep").GetProperty("ativo").GetBoolean() ? null : "pep.ativo falso"));
            await Conferir("mesmos 6 dígitos, outro nome -> liberar", HttpStatusCode.OK,
                new { documento = cpf, nome = "Nome Que Nao Existe" }, true, j => Resultado(j, "liberar"));
        }

        Console.WriteLine(falhas == 0 ? "\n[ok] todas as chamadas responderam como esperado" : $"\n[ATENÇÃO] {falhas} falha(s)");
        return falhas == 0 ? 0 : 1;
    }

    /// <summary>CPF válido "123" + miolo + dígitos verificadores.</summary>
    private static string CpfComMiolo(string miolo)
    {
        var digitos = ("123" + miolo).Select(c => c - '0').ToList();
        foreach (var posicao in (int[])[9, 10])
        {
            var soma = 0;
            for (var i = 0; i < posicao; i++)
            {
                soma += digitos[i] * (posicao + 1 - i);
            }

            digitos.Add(soma * 10 % 11 % 10);
        }

        return string.Concat(digitos);
    }
}
