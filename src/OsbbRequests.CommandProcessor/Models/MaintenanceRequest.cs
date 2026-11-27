using OsbbRequests.Domain.Models;

namespace OsbbRequests.CommandProcessor.Models;

public sealed record CommandOutcome(string Reply, MaintenanceRequest? NewRequest = null,
    RegistrationChange? Registration = null, ReplyKeyboard? Keyboard = null, RequestDraftChange? Draft = null,
    RequestLookupChange? Lookup = null, RequestDeletion? Deletion = null, ProfileDialogChange? ProfileDialog = null, ProfileUpdate? Profile = null,
    int? OwnerProfileVersion = null, string? NextPageCommand = null, PageNavigationChange? PageNavigation = null,
    ResidentComment? Comment = null, ApartmentSubmission? Apartment = null, RequestListNavigation? RequestList = null);
public sealed record ApartmentSubmission(OsbbRequests.Domain.Models.OwnerApartments Value, int? ExpectedVersion, int ProfileVersion, string ApartmentId);
public sealed record ResidentComment(long RequestId, long OwnerId, string ComplexId, string Text, DateTimeOffset CreatedAt);
public sealed record StoredReply(long UpdateId, long UserId, long ChatId, string Text, bool Delivered,
    ReplyKeyboard? Keyboard = null, bool RefreshMenu = false);
