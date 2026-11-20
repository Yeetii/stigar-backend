using System.Net;
using Hangfire.Common;
using Hangfire.States;
using Hangfire;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stigar.Core.Constants;
using Stigar.Core.Jobs;
using Stigar.Core.Models;
using Stigar.Core.Services;
using Stigar.Core.Services.Postgres;
using Stigar.Core.Services.StravaClient;
using Stigar.Api.Endpoints.User;

namespace Stigar.Workers.Tests;

public class DeleteAccountEndpointTests
{
    [Fact]
    public async Task Handle_WithValidSession_QueuesDeleteJobAndClearsSessionCookie()
    {
        var user = new User { Id = "user-123" };
        var authService = CreateUserAuthenticationService("session-123", user);
        var jobs = new Mock<IBackgroundJobClient>();
        var context = CreateContext("session-123");

        var endpoint = CreateEndpoint(authService, jobs.Object, new CapturingHandler());
        var result = await endpoint.Handle(context, CancellationToken.None);

        Assert.Equal(StatusCodes.Status204NoContent, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        jobs.Verify(j => j.Create(It.Is<Job>(job =>
            job.Method.Name == "ExecuteAsync" && job.Queue == QueueConfig.AccountDeleteJobs.ToLowerInvariant()),
            It.IsAny<IState>()), Times.Once);
        Assert.Contains("session=", context.Response.Headers["Set-Cookie"].ToString());
    }

    [Fact]
    public async Task Handle_WithoutValidSession_ReturnsUnauthorized()
    {
        var authService = CreateUserAuthenticationService("session-123", null);
        var jobs = new Mock<IBackgroundJobClient>();
        var context = CreateContext(null);

        var endpoint = CreateEndpoint(authService, jobs.Object, new CapturingHandler());
        var result = await endpoint.Handle(context, CancellationToken.None);

        Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        jobs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Handle_WithRefreshToken_RevokesStravaGrant()
    {
        var user = new User { Id = "user-123", RefreshToken = "refresh-token-abc" };
        var authService = CreateUserAuthenticationService("session-123", user);
        var jobs = new Mock<IBackgroundJobClient>();
        var context = CreateContext("session-123");
        var handler = new CapturingHandler();

        var endpoint = CreateEndpoint(authService, jobs.Object, handler);
        await endpoint.Handle(context, CancellationToken.None);

        Assert.NotNull(handler.Request);
        Assert.Equal(HttpMethod.Post, handler.Request.Method);
        Assert.Equal("https://www.strava.com/oauth/revoke", handler.Request.RequestUri?.ToString());
        Assert.StartsWith("Basic ", handler.Request.Headers.Authorization?.ToString());
        Assert.Contains("token=refresh-token-abc", handler.Body);
        Assert.Contains("token_type_hint=refresh_token", handler.Body);
    }

    [Fact]
    public async Task Handle_WhenStravaRevokeFails_StillQueuesDeleteJob()
    {
        var user = new User { Id = "user-123", RefreshToken = "refresh-token-abc" };
        var authService = CreateUserAuthenticationService("session-123", user);
        var jobs = new Mock<IBackgroundJobClient>();
        var context = CreateContext("session-123");

        var endpoint = CreateEndpoint(authService, jobs.Object, new ThrowingHandler());
        var result = await endpoint.Handle(context, CancellationToken.None);

        Assert.Equal(StatusCodes.Status204NoContent, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Once);
        Assert.Contains("session=", context.Response.Headers["Set-Cookie"].ToString());
    }

    private static DeleteAccount CreateEndpoint(
        UserAuthenticationService authService,
        IBackgroundJobClient jobs,
        HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["StravaClientSecret"] = "test-secret" })
            .Build();
        var authApi = new AuthenticationApi(new HttpClient(handler), configuration);
        return new DeleteAccount(authService, jobs, authApi, NullLogger<DeleteAccount>.Instance);
    }

    private static DefaultHttpContext CreateContext(string? sessionId)
    {
        var context = new DefaultHttpContext();
        if (sessionId is not null)
            context.Request.Headers["Cookie"] = $"session={sessionId}";
        return context;
    }

    private static UserAuthenticationService CreateUserAuthenticationService(string? sessionId, User? user)
    {
        var sessions = new Mock<ISessionRepository>();
        sessions
            .Setup(r => r.GetById(sessionId!, It.IsAny<CancellationToken>()))
            .ReturnsAsync(sessionId == null ? null : new Session { Id = sessionId, UserId = user?.Id ?? "user-123" });

        var users = new Mock<IUserRepository>();
        users
            .Setup(r => r.GetById(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        return new UserAuthenticationService(users.Object, sessions.Object);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("Strava unavailable");
    }
}
