using System.Text.RegularExpressions;
using FluentValidation;
using MotorCredito.Api.Contracts;
using MotorCredito.Core.Documentos;

namespace MotorCredito.Api.Validation;

public sealed partial class EntradaTriagemValidator : AbstractValidator<EntradaTriagem>
{
    public EntradaTriagemValidator()
    {
        // A entrada já chega sem espaços nas extremidades (ver TriagemEndpoints).
        RuleFor(x => x.Documento)
            .NotEmpty()
            .Length(11, 20)
            .Must(documento => ValidadorDeDocumento.TentarNormalizar(documento, out _))
            .WithMessage("documento não é um CPF ou CNPJ válido");

        RuleFor(x => x.Nome)
            .MaximumLength(200)
            .Must(nome => nome is null || !CaracteresDeControle().IsMatch(nome))
            .WithMessage("nome contém caracteres de controle");
    }

    [GeneratedRegex(@"[\x00-\x1F\x7F]")]
    private static partial Regex CaracteresDeControle();
}
