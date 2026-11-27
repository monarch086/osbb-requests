using OsbbRequests.Domain;
using OsbbRequests.Domain.Models;
using OsbbRequests.Services.Notifications;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class AdministratorNotificationTests
{
    [Theory]
    [InlineData(ResidentType.Owner, "Власник", "4Б", RegistrationStage.Pending)]
    [InlineData(ResidentType.FamilyMember, "Член родини", "4В", RegistrationStage.Approved)]
    [InlineData(ResidentType.Tenant, "Орендар", "4Г", RegistrationStage.Approved)]
    [InlineData(ResidentType.Employee, "Працівник ОСББ", null, RegistrationStage.Pending)]
    public void NewResidentAlertContainsTypeAndHouse(ResidentType type, string label, string? house, RegistrationStage stage)
    {
        var resident = new ResidentRegistration("reference", 987654321, "complex", stage, 1, Now, Now,
            Phone: "+380500000000", FirstName: "Олена", LastName: "Тестова", BuildingNumber: house,
            ApartmentNumber: "125", Email: "resident@example.invalid", ResidentType: type);
        var alert = AdministratorNotifications.NewResident(resident);
        Assert.Contains("Тип мешканця: " + label, alert.Text);
        Assert.Contains("Будинок: " + (house ?? "Не зазначено"), alert.Text);
        Assert.Contains(stage == RegistrationStage.Approved ? "схвалено автоматично" : "Потребує перевірки", alert.Text);
        Assert.DoesNotContain("987654321", alert.Text);
        Assert.DoesNotContain(resident.Phone!, alert.Text);
        Assert.DoesNotContain(resident.Email!, alert.Text);
        Assert.Equal(AdministratorEvent.NewResident, alert.AdministratorEvent);
        Assert.True(alert.IsBroadcast);
    }

    [Theory]
    [InlineData(RequestVisibility.Public)]
    [InlineData(RequestVisibility.Private)]
    public void NewRequestAlertIncludesFullDescriptionButNoOtherPrivateFields(RequestVisibility visibility)
    {
        const string description = "Зламано освітлення\nБіля другого ліфта 🛠 <b>Перевірити</b>";
        var request = new MaintenanceRequest(7, 987654321, "complex", "електрика", "", description, Now, Visibility: visibility,
            Resolution: "Приватне пояснення", AssignedTo: "private-assignee", Scope: RequestScope.Building, BuildingNumber: "4Д", Floor: 12, Entrance: 2);
        var alert = AdministratorNotifications.NewRequest(request);
        Assert.Contains("\n~~~~~\n" + description + "\n~~~~~\n", alert.Text);
        Assert.Contains("Нова заявка №7", alert.Text);
        Assert.Contains("Будинок 4Д", alert.Text);
        Assert.DoesNotContain("987654321", alert.Text);
        Assert.DoesNotContain("Приватне пояснення", alert.Text);
        Assert.DoesNotContain("private-assignee", alert.Text);
        Assert.Equal(AdministratorEvent.NewComplexRequest, alert.AdministratorEvent);
        Assert.True(alert.IsBroadcast);
    }

    [Fact]
    public void MaximumDescriptionFitsTelegram()
    {
        var description = string.Concat(Enumerable.Repeat("🛠", 1000));
        var request = new MaintenanceRequest(long.MaxValue, 42, "complex", "місця загального користування", "", description, Now,
            Scope: RequestScope.Building, BuildingNumber: "4Д", Floor: 25, Entrance: 2);
        var alert = AdministratorNotifications.NewRequest(request);
        Assert.Contains(description, alert.Text);
        Assert.InRange(alert.Text.Length, 1, 4096);
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-23T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
}
