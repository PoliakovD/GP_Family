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

    private async Task<AuthenticateResult> AuthenticateAsync(string authorizationHeader)
    {
        var optionsMonitor = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        optionsMonitor.CurrentValue.Returns(new AuthenticationSchemeOptions());
        optionsMonitor.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());

        var handler = new TelegramMiniAppAuthenticationHandler(
            optionsMonitor, NullLoggerFactory.Instance, UrlEncoder.Default, _validator, _provisioning);

        var scheme = new AuthenticationScheme(AuthSchemes.TelegramMiniApp, null, typeof(TelegramMiniAppAuthenticationHandler));
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = authorizationHeader;

        await handler.InitializeAsync(scheme, context);
        return await handler.AuthenticateAsync();
    }

    [Fact]
    public async Task Authenticate_UnboundTelegramId_FailsAndNeverAutoCreates()
    {
        _validator.Validate(Arg.Any<string>()).Returns(new TelegramInitDataResult(999, "Someone", null));
        _provisioning.GetUserIdByTelegramIdAsync(999, Arg.Any<CancellationToken>()).Returns((Guid?)null);

        var result = await AuthenticateAsync("tma fake-init-data");

        result.Succeeded.Should().BeFalse();
        await _provisioning.DidNotReceive().GetOrCreateUserIdAsync(
            Arg.Any<long>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Authenticate_BoundTelegramId_SucceedsWithExpectedClaims()
    {
        var userId = Guid.NewGuid();
        _validator.Validate(Arg.Any<string>()).Returns(new TelegramInitDataResult(999, "Someone", null));
        _provisioning.GetUserIdByTelegramIdAsync(999, Arg.Any<CancellationToken>()).Returns(userId);

        var result = await AuthenticateAsync("tma fake-init-data");

        result.Succeeded.Should().BeTrue();
        result.Principal!.FindFirst(FamilyHubClaimTypes.UserId)!.Value.Should().Be(userId.ToString());
        result.Principal!.FindFirst(FamilyHubClaimTypes.TelegramId)!.Value.Should().Be("999");
        await _provisioning.DidNotReceive().GetOrCreateUserIdAsync(
            Arg.Any<long>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Authenticate_InvalidInitData_Fails()
    {
        _validator.Validate(Arg.Any<string>()).Returns((TelegramInitDataResult?)null);

        var result = await AuthenticateAsync("tma garbage");

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Authenticate_MissingHeader_Fails()
    {
        var result = await AuthenticateAsync(string.Empty);

        result.Succeeded.Should().BeFalse();
    }
}
