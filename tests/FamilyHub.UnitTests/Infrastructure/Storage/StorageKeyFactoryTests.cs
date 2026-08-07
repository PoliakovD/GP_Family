using FamilyHub.Infrastructure.Storage;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Storage;

public class StorageKeyFactoryTests
{
    [Fact]
    public void Create_IsDeterministic_ForSameId()
    {
        var id = Guid.NewGuid();

        StorageKeyFactory.Create(id).Should().Be(StorageKeyFactory.Create(id));
    }

    [Fact]
    public void Create_DifferentIds_ProduceDifferentKeys()
    {
        StorageKeyFactory.Create(Guid.NewGuid()).Should().NotBe(StorageKeyFactory.Create(Guid.NewGuid()));
    }

    [Fact]
    public void Create_HasTwoLevelShardPrefix_MatchingFirstBytesOfId()
    {
        var id = Guid.NewGuid();
        var hex = id.ToString("N");

        StorageKeyFactory.Create(id).Should().Be($"blobs/{hex[..2]}/{hex[2..4]}/{hex}");
    }

    [Fact]
    public void Create_KeyCarriesNoSemanticsBeyondTheAttachmentId()
    {
        // Ключ обязан быть непрозрачным: единственная информация в нём — сам attachmentId,
        // ничего о владельце/записи/виде документа (см. StorageKeyFactory docblock).
        var id = Guid.NewGuid();

        var key = StorageKeyFactory.Create(id);

        key.Should().StartWith("blobs/");
        key.Should().EndWith(id.ToString("N"));
        key.Split('/').Should().HaveCount(4);
    }
}
