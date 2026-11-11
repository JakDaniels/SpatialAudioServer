using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SpatialAudioServer.Rooms;

namespace SpatialAudioServer.Api;

/// <summary>Maps domain exceptions to RFC 9457 problem responses: 400 validation, 404 not found, 409 limit reached.</summary>
internal sealed class DomainExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        ProblemDetails? details = exception switch
        {
            DomainValidationException v => new ValidationProblemDetails(v.Errors) { Status = StatusCodes.Status400BadRequest },
            NotFoundException => new ProblemDetails { Status = StatusCodes.Status404NotFound, Title = "Not found", Detail = exception.Message },
            LimitExceededException => new ProblemDetails { Status = StatusCodes.Status409Conflict, Title = "Limit reached", Detail = exception.Message },
            ConflictException => new ProblemDetails { Status = StatusCodes.Status409Conflict, Title = "Conflict", Detail = exception.Message },
            Media.MediaUnavailableException => new ProblemDetails { Status = StatusCodes.Status503ServiceUnavailable, Title = "Media unavailable", Detail = exception.Message },
            UnauthorizedAccessException => new ProblemDetails { Status = StatusCodes.Status401Unauthorized, Title = "Unauthorized", Detail = exception.Message },
            ArgumentException => new ProblemDetails { Status = StatusCodes.Status400BadRequest, Title = "Bad request", Detail = exception.Message },
            _ => null,
        };
        if (details is null)
        {
            return false;
        }

        context.Response.StatusCode = details.Status!.Value;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = details,
            Exception = exception,
        });
    }
}
