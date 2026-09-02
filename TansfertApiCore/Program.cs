
using Microsoft.AspNetCore.Http.Extensions;
using TansfertApiCore;
using WindowsWifiLibrary.Core.Models;

var builder = WebApplication.CreateBuilder(args);

var transferHost = builder.Configuration["TransferApi:Host"] ?? "127.0.0.1";
var transferPort = builder.Configuration.GetValue<int>("TransferApi:Port", 5060);
builder.WebHost.UseUrls($"http://{transferHost}:{transferPort}");

builder.Services.AddSingleton<ITransferShareService, TransferShareService>();
builder.Services.AddSingleton<ITransferApiService>(sp => new TransferApiService(sp.GetRequiredService<ITransferShareService>()));

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/transfer/request", async (TransferSessionMetadata metadata, ITransferApiService transferApiService, CancellationToken cancellationToken) =>
{
    if (metadata is null)
    {
        return Results.BadRequest(new { error = "La session de transfert est vide." });
    }

    var session = await transferApiService.RegisterOutgoingRequestAsync(metadata, cancellationToken);
    return Results.Ok(session);
});

app.MapGet("/api/transfer/session/{sessionId}", async (string sessionId, ITransferApiService transferApiService, CancellationToken cancellationToken) =>
{
    var session = await transferApiService.GetSessionAsync(sessionId, cancellationToken);
    return session is null ? Results.NotFound() : Results.Ok(session);
});

app.MapPost("/api/transfer/accept", async (TransferAcceptanceRequest request, ITransferApiService transferApiService, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.SessionId))
    {
        return Results.BadRequest(new { error = "sessionId est requis." });
    }

    var decision = await transferApiService.AcceptTransferAsync(request.SessionId, request.DestinationFolder, cancellationToken);
    return Results.Ok(decision);
});

app.MapGet("/api/transfer/download", async (HttpContext httpContext, ITransferApiService transferApiService, CancellationToken cancellationToken) =>
{
    var sessionId = httpContext.Request.Query["sessionId"].ToString();
    var relativePath = httpContext.Request.Query["path"].ToString();

    var file = await transferApiService.OpenDownloadAsync(sessionId, relativePath, cancellationToken);
    if (file is null)
    {
        return Results.NotFound(new { error = "Le fichier demandé est introuvable." });
    }

    await using var stream = file.Content;
    return Results.File(stream, "application/octet-stream", file.FileName, enableRangeProcessing: true);
});

app.Run();
