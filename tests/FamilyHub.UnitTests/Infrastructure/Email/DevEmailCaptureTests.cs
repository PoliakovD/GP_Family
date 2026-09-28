using FamilyHub.Infrastructure.Email;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Email;

/// <summary>Перехват писем LoggingEmailSender (см. class doc DevEmailCapture) — GET /dev/last-otp
/// читает отсюда, дав e2e (реальный API-процесс, не WebApplicationFactory) программный доступ к
/// коду подтверждения без похода в почту/лог (TECH_DEBT.md #11).</summary>
public class DevEmailCaptureTests
{
    private readonly DevEmailCapture _sut = new();

    [Fact]
    public void LastCodeFor_NoMessagesSent_ReturnsNull()
    {
        _sut.LastCodeFor("nobody@example.com").Should().BeNull();
    }

    [Fact]
    public void LastCodeFor_ExtractsSixDigitCodeFromBody()
    {
        _sut.Record("a@example.com", "Ваш код подтверждения: 482915\nКод действителен 10 минут.");

        _sut.LastCodeFor("a@example.com").Should().Be("482915");
    }

    [Fact]
    public void LastCodeFor_TwoMessagesToSameAddress_ReturnsTheLastOnes()
    {
        // Живой сценарий: TelegramBindingService может отправить два письма подряд на один
        // адрес (OTP-код привязки, потом временный пароль нового аккаунта) — нужен код именно
        // из последнего письма, не из первого совпадения \d{6} по всей истории.
        _sut.Record("a@example.com", "Ваш код подтверждения: 111111");
        _sut.Record("a@example.com", "Ваш код подтверждения: 222222");

        _sut.LastCodeFor("a@example.com").Should().Be("222222");
    }

    [Fact]
    public void LastCodeFor_IsCaseInsensitiveOnEmail()
    {
        _sut.Record("Someone@Example.com", "Ваш код подтверждения: 555555");

        _sut.LastCodeFor("someone@example.com").Should().Be("555555");
    }
}
