using Microsoft.AspNetCore.Http;

namespace Tilsynsvakt.Api;

public sealed class ApiException(
    int status,
    string code,
    string title,
    string detail,
    ShiftDto? currentShift = null,
    int? retryAfterSeconds = null)
    : Exception(detail)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string Title { get; } = title;
    public string Detail { get; } = detail;
    public ShiftDto? CurrentShift { get; } = currentShift;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;

    public IResult ToResult()
    {
        var extensions = new Dictionary<string, object?> { ["code"] = Code };
        if (CurrentShift is not null)
        {
            extensions["currentShift"] = CurrentShift;
        }

        return Results.Problem(detail: Detail, statusCode: Status, title: Title, type: "about:blank", extensions: extensions);
    }
}

public static class Errors
{
    public static ApiException InvalidDate() =>
        new(400, "invalid_date", "Ugyldig dato", "Bruk datoformatet yyyy-MM-dd.");

    public static ApiException InvalidRange() =>
        new(400, "invalid_range", "Ugyldig datoperiode", "Fra-dato må være før eller lik til-dato, og perioden kan være høyst 366 dager.");

    public static ApiException InvalidBody() =>
        new(400, "invalid_body", "Ugyldig forespørsel", "Innholdet er ikke gyldig JSON, eller påkrevde felt mangler eller har feil type.");

    public static ApiException InvalidId() =>
        new(400, "invalid_id", "Ugyldig id", "Id må være et positivt heltall.");

    public static ApiException InvalidName() =>
        new(422, "invalid_name", "Ugyldig navn", "Navnet må ha 2–80 tegn, begynne med en bokstav og bare inneholde bokstaver, mellomrom, apostrof, punktum eller bindestrek.");

    public static ApiException InvalidPhone() =>
        new(422, "invalid_phone", "Ugyldig telefonnummer", "Oppgi et norsk telefonnummer med 8 siffer, eventuelt med +47.");

    public static ApiException NotAShiftDay(int status) =>
        new(status, "not_a_shift_day", "Ikke en vaktdag", "Det er bare vakter tirsdag til torsdag i skoleperiodene.");

    public static ApiException DateInPast() =>
        new(422, "date_in_past", "Dato i fortiden", "Vakter i fortiden kan ikke endres.");

    public static ApiException DateTooFarAhead() =>
        new(422, "date_too_far_ahead", "Dato for langt frem i tid", "Vakter kan bare endres inntil 400 dager frem i tid.");

    public static ApiException UnknownGuard() =>
        new(422, "unknown_guard", "Ukjent tilsynsvakt", "Valgt tilsynsvakt finnes ikke eller er ikke aktiv.");

    public static ApiException GuardNotFound() =>
        new(404, "guard_not_found", "Fant ikke tilsynsvakt", "Tilsynsvakten finnes ikke.");

    public static ApiException GuardNameTaken() =>
        new(409, "guard_name_taken", "Navnet er i bruk", "Det finnes allerede en tilsynsvakt med dette navnet.");

    public static ApiException ShiftNotTaken() =>
        new(404, "shift_not_taken", "Vakten er ledig", "Ingen har tatt denne vakten.");

    public static ApiException ShiftTaken(ShiftDto current) =>
        new(409, "shift_taken", "Vakten er tatt", "Noen andre har allerede tatt denne vakten.", current);

    public static ApiException ShiftChanged(ShiftDto current) =>
        new(409, "shift_changed", "Vakten er endret", "Vakten har blitt endret av noen andre. Last inn på nytt.", current);

    public static ApiException SwapRequestNotFound() =>
        new(404, "swap_request_not_found", "Fant ikke forespørselen", "Byteforespørselen finnes ikke lenger.");

    public static ApiException SwapForbidden() =>
        new(403, "swap_forbidden", "Ikke tillatt", "Bare tilsynsvakten forespørselen er sendt til kan godkjenne den, og bare de to involverte kan avslå eller trekke den.");

    public static ApiException SwapInvalid() =>
        new(422, "swap_invalid", "Ugyldig bytte", "Du kan ikke bytte vakt med deg selv eller bytte en vakt med seg selv.");

    public static ApiException Unauthorized() =>
        new(401, "unauthorized", "Ikke autorisert", "Manglende eller ugyldig nøkkel.");

    public static ApiException UnsupportedMedia() =>
        new(415, "unsupported_media_type", "Ugyldig innholdstype", "Bruk Content-Type: application/json.");

    public static ApiException RequestTooLarge() =>
        new(413, "request_too_large", "Forespørselen er for stor", "Innholdet kan være høyst 2 KB.");

    public static ApiException PreconditionRequired() =>
        new(428, "precondition_required", "Forutsetning mangler", "Oppgi expectedGuardId for å fjerne en vakt som er tatt.");

    public static ApiException RateLimited() =>
        new(429, "rate_limited", "For mange forespørsler", "Vent litt og prøv igjen.");

    public static ApiException StorageBusy() =>
        new(503, "storage_busy", "Lagring midlertidig utilgjengelig", "Lagringen er under høy belastning. Vent litt og prøv igjen.", retryAfterSeconds: 1);

    public static ApiException Internal() =>
        new(500, "internal_error", "Intern feil", "Noe gikk galt. Prøv igjen senere.");
}
