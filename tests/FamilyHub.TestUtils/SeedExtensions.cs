using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;

namespace FamilyHub.TestUtils;

/// <summary>Удобные комбинированные сидеры поверх TestData — типичные сценарии "семья + участники".</summary>
public static class SeedExtensions
{
    /// <summary>Создаёт семью с одним Active Admin'ом. Сохраняет сразу.</summary>
    public static (Family Family, User Admin) SeedFamilyWithAdmin(this AppDbContext db, string? familyName = null)
    {
        var admin = TestData.NewUser();
        var family = TestData.NewFamily(familyName);

        db.Users.Add(admin);
        db.Families.Add(family);
        db.FamilyMembers.Add(TestData.NewMember(family.Id, admin.Id, FamilyRole.Admin, MemberStatus.Active));
        db.SaveChanges();

        return (family, admin);
    }

    /// <summary>
    /// Создаёт и сохраняет "ничейного" пользователя — для сценариев, где нужен валидный
    /// User.Id (FamilyMember.UserId — FK на Users), но без членства ни в одной семье
    /// (например, будущий редимящий инвайт).
    /// </summary>
    public static User AddUser(this AppDbContext db)
    {
        var user = TestData.NewUser();
        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }

    /// <summary>Добавляет нового пользователя как члена существующей семьи с заданной ролью/статусом.</summary>
    public static User AddMember(
        this AppDbContext db, Guid familyId, FamilyRole role = FamilyRole.Member, MemberStatus status = MemberStatus.Active)
    {
        var user = TestData.NewUser();
        db.Users.Add(user);
        db.FamilyMembers.Add(TestData.NewMember(familyId, user.Id, role, status));
        db.SaveChanges();

        return user;
    }
}
