using System.Text.Json.Serialization;

namespace OsbbRequests.Domain.Models;

[JsonConverter(typeof(JsonStringEnumConverter<ResidentType>))]
public enum ResidentType { FamilyMember, Owner, Tenant, Employee }
