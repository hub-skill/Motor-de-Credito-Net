# Imagem de produção do Motor de Crédito .NET (ASP.NET Core).
# Build (na raiz da solução):  docker build -t motor-credito-net:0.1.0 .
# Rodar:   ver DOCUMENTACAO.md, seção Operação (env file sem credenciais de administrador).
# Tags fixas (nunca latest). A ferramenta de carga (MotorCredito.Carga) não entra na imagem.

# SDK: as imagens oficiais trazem a faixa 10.0.1xx; o global.json (10.0.100 + rollForward latestFeature) aceita.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restore primeiro: a camada só é refeita quando projetos/pacotes mudam.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/MotorCredito.Api/MotorCredito.Api.csproj src/MotorCredito.Api/
COPY src/MotorCredito.Core/MotorCredito.Core.csproj src/MotorCredito.Core/
COPY src/MotorCredito.Dados/MotorCredito.Dados.csproj src/MotorCredito.Dados/
RUN dotnet restore src/MotorCredito.Api/MotorCredito.Api.csproj
COPY src/MotorCredito.Api/ src/MotorCredito.Api/
COPY src/MotorCredito.Core/ src/MotorCredito.Core/
COPY src/MotorCredito.Dados/ src/MotorCredito.Dados/
RUN dotnet publish src/MotorCredito.Api/MotorCredito.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false

# Chiseled: sem shell, sem gerenciador de pacotes, usuário não root (app, UID 1654) por padrão.
# "-extra" traz ICU e tzdata: culturas pt-BR, fusos horários e o ODP.NET, que depende deles.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=1
USER $APP_UID
EXPOSE 8080
# Sem HEALTHCHECK: a imagem chiseled não tem curl; quem verifica a saúde são as probes do Kubernetes.
ENTRYPOINT ["dotnet", "MotorCredito.Api.dll"]
