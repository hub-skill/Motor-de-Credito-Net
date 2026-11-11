namespace MotorCredito.Api.Middleware;

public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
        headers["Cache-Control"] = "no-store"; // a resposta contém dados pessoais
        headers["X-Request-ID"] = context.TraceIdentifier;
        return next(context);
    }
}
