using System.Text.Encodings.Web;
using FamilyHub.Infrastructure.Auth;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.CurrentUser;
using FamilyHub.Infrastructure.Telegram;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Auth;

/// <summary>
/// Lookup-only: центральная защита от "голых" Telegram-аккаунтов без email (см. план
/// email-as-anchor). Раньше здесь был get-or-create — любой валидный initData от ещё не
/// привязанного TelegramId молча создавал User. Теперь такой запрос должен провалиться,
/// и, что важнее, НЕ вызвать GetOrCreateUserIdAsync ни при каких обстоятельствах.
/// </summary>
public class TelegramMiniAppAuthenticationHandlerTests
{
    private readonly ITelegramInitDataValidator _validator = Substitute.For<ITelegramInitDataValidator>();
    private readonly IUserProvisioningService _provisioning = Substitute.For<IUserProvisioningService>();

    private async Task<AuthenticateResult> AuthenticateAsync(string authorizationHeader) =>
        (await CreateAuthenticatedHandlerAsync(authorizationHeader)).Result;

    private async Task<(AuthenticateResult Result, TelegramMiniAppAuthenticationHandler Handler, DefaultHttpContext Context)>
        CreateAuthenticatedHandlerAsync(string authorizationHeader)
    {
        var optionsMonitor = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        optionsMonitor.CurrentValue.Returns(new AuthenticationSchemeOptions());
        optionsMonitor.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());

        var handler = new TelegramMiniAppAuthenticationHandler(
            optionsMonitor, NullLoggerFactory.Instance, UrlEncoder.Default, _validator, _provisioning);

        var scheme = new AuthenticationScheme(AuthSchemes.TelegramMiniApp, null, typeof(TelegramMiniAppAuthenticationHandler));
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = authorizationHeader;

        context.Response.Body = new MemoryStream();

        await handler.InitializeAsync(scheme, context);
        return (await handler.AuthenticateAsync(), handler, context);
    }

    [Fact]
    public async Task Authenticate_UnboundTelegramId_FailsAndNeverAutoCreates()
    {
        _validator.Check(Arg.Any<string>()).Returns(new TelegramInitDataCheck(new TelegramInitDataResult(999, "Someone", null), TelegramInitDataFailure.None));
        _provisioning.GetUserIdByTelegramIdAsync(999, Arg.Any<CancellationToken>()).Returns((Guid?)null);

        var result = await AuthenticateAsync("tma fake-init-data");

        result.Succeeded.Should().BeFalse();
        await _provisioning.DidNotReceive().GetOrCreateUserIdAsync(
            Arg.Any<long>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Authenticate_BoundTelegramId_SucceedsWithExpectedClaims()
    {
        var userId = Guid.NewGuid();
        _validator.Check(Arg.Any<string>()).Returns(new TelegramInitDataCheck(new TelegramInitDataResult(999, "Someone", null), TelegramInitDataFailure.None));
        _provisioning.GetUserIdByTelegramIdAsync(999, Arg.Any<CancellationToken>()).Returns(userId);

        var result = await AuthenticateAsync("tma fake-init-data");

        result.Succeeded.Should().BeTrue();
        result.Principal!.FindFirst(FamilyHubClaimTypes.UserId)!.Value.Should().Be(userId.ToString());
        result.Principal!.FindFirst(FamilyHubClaimTypes.TelegramId)!.Value.Should().Be("999");
        await _provisioning.DidNotReceive().GetOrCreateUserIdAsync(
            Arg.Any<long>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Authenticate_InvalidInitData_Fails()
    {
        _validator.Check(Arg.Any<string>()).Returns(TelegramInitDataCheck.Invalid);

        var result = await AuthenticateAsync("tma garbage");

        result.Succeeded.Should().BeFalse();
    }

    // Регрессия на находку M6 (docs/security/security-audit-2026-10.md): просроченная initData —
    // это «перезапустите Mini App», а не «аккаунт не привязан»: фронт различает их по коду.
    [Fact]
    public async Task ExpiredInitData_FailsAndChallengeReturnsInitDataExpiredCode()
    {
        _validator.Check(Arg.Any<string>()).Returns(TelegramInitDataCheck.Expired);

        var (result, handler, context) = await CreateAuthenticatedHandlerAsync("tma old-init-data");
        await handler.ChallengeAsync(new AuthenticationProperties());

        result.Succeeded.Should().BeFalse();
        await _provisioning.DidNotReceive().GetUserIdByTelegramIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        context.Response.Body.Position = 0;
        (await new StreamReader(context.Response.Body).ReadToEndAsync()).Should().Contain("init_data_expired");
    }

    [Fact]
    public async Task InvalidInitData_ChallengeHasNoExpiredCode()
    {
        _validator.Check(Arg.Any<string>()).Returns(TelegramInitDataCheck.Invalid);

        var (_, handler, context) = await CreateAuthenticatedHandlerAsync("tma garbage");
        await handler.ChallengeAsync(new AuthenticationProperties());

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        context.Response.Body.Length.Should().Be(0);
    }

    [Fact]
    public async Task Authenticate_MissingHeader_Fails()
    {
        var result = await AuthenticateAsync(string.Empty);

        result.Succeeded.Should().BeFalse();
    }
}
