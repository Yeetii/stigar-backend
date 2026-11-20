using Stigar.Api.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stigar.Core.Services;

namespace Stigar.Api.Endpoints.GarminCourses;

internal static class GarminCourseAuthorization
{
    private const string DefaultAllowedUserIds = "11908635";

    public static async Task<IResult?> Authorize(
        HttpContext context,
        UserAuthenticationService userAuthService)
    {
        var user = await userAuthService.GetUserFromSessionId(context.SessionId());
        if (user == null)
            return TypedResults.Unauthorized();

        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        var allowed = (configuration.GetValue<string>("GarminAllowedUserIds") ?? DefaultAllowedUserIds)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (!allowed.Contains(user.Id, StringComparer.Ordinal))
            return TypedResults.StatusCode(StatusCodes.Status403Forbidden);

        return null;
    }
}
