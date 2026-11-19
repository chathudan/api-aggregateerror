// Test data for trying "Combine lists", the via mapping and duplicate removal (services E, F, G, H).
// Invented people, example.com emails and 555-01xx phone numbers (reserved for fiction). No real data.
//
// This file has no dependencies on purpose: the engine tests link it (see ApiAggregator.Engine.Tests.csproj),
// so the tests and the running mocks always use exactly the same records. samples/README.md explains
// what each odd record is for and what the sample recipes return.

namespace MockServices;

public static class PeopleSamples
{
    /// <summary>GET /e/users (Identity). The system of record for people: ids "u-1".."u-6".</summary>
    public const string Users = """
    {
      "users": [
        { "id": "u-1", "name": "Ava Thompson",    "email": "ava.thompson@example.com",    "updatedAt": "2026-03-01T09:00:00Z" },
        { "id": "u-2", "name": "Ben Okafor",      "email": "ben.okafor@example.com",      "updatedAt": "2026-03-02T09:00:00Z" },
        { "id": "u-3", "name": "Chloe Nguyen",    "email": "chloe.nguyen@example.com",    "updatedAt": "2026-01-10T09:00:00Z" },
        { "id": "u-4", "name": "Daniel Kowalski", "email": "daniel.kowalski@example.com", "updatedAt": "2026-01-12T09:00:00Z" },
        { "id": "u-5", "name": "Emma Rossi",      "email": "emma.rossi@example.com",      "updatedAt": "2026-02-20T09:00:00Z" },
        { "id": "u-6", "name": "Farid Haddad",    "email": "farid.haddad@example.com",    "updatedAt": "2026-02-21T09:00:00Z" }
      ]
    }
    """;

    /// <summary>
    /// GET /f/contacts (CRM). Own ids "c-101".., linked to Identity by identityUserId. Odd rows on purpose:
    ///   c-102  identityUserId " U-2"   spacing and case: matches u-2 only with normalize trim + lower
    ///   c-103  newer, different name   wins the name with onConflict newest:updatedAt
    ///   c-104  different email, newer  email still comes from Identity (owners)
    ///   c-104  exact duplicate row     removed by | distinct
    ///   c-105  identityUserId null     no match value: kept on its own (or dropped with withoutKey drop)
    ///   c-106  identityUserId "u-9"    a person Identity doesn't have: appended as its own record
    ///   c-107  second contact for u-1, newest of all, phone null: a null never overwrites c-101's phone
    /// </summary>
    public const string Contacts = """
    {
      "contacts": [
        { "id": "c-101", "identityUserId": "u-1",  "name": "Ava Thompson",       "email": "ava.thompson@example.com",  "phone": "+1 202 555 0101", "updatedAt": "2026-02-01T08:00:00Z" },
        { "id": "c-102", "identityUserId": " U-2", "name": "Ben Okafor",         "email": "ben.okafor@example.com",    "phone": null,              "updatedAt": "2026-02-03T08:00:00Z" },
        { "id": "c-103", "identityUserId": "u-3",  "name": "Chloe Nguyen-Smith", "email": "chloe.nguyen@example.com",  "phone": "+1 202 555 0103", "updatedAt": "2026-04-15T08:00:00Z" },
        { "id": "c-104", "identityUserId": "u-4",  "name": "Daniel Kowalski",    "email": "dan.k@example.com",         "phone": "+1 202 555 0104", "updatedAt": "2026-04-20T08:00:00Z" },
        { "id": "c-104", "identityUserId": "u-4",  "name": "Daniel Kowalski",    "email": "dan.k@example.com",         "phone": "+1 202 555 0104", "updatedAt": "2026-04-20T08:00:00Z" },
        { "id": "c-105", "identityUserId": null,   "name": "Grace Lee",          "email": "grace.lee@example.com",     "phone": null,              "updatedAt": "2026-01-05T08:00:00Z" },
        { "id": "c-106", "identityUserId": "u-9",  "name": "Hugo Martin",        "email": "hugo.martin@example.com",   "phone": "+1 202 555 0106", "updatedAt": "2026-03-10T08:00:00Z" },
        { "id": "c-107", "identityUserId": "u-1",  "name": "Ava Thompson",       "email": "ava.thompson@example.com",  "phone": null,              "updatedAt": "2026-05-01T08:00:00Z" }
      ]
    }
    """;

    /// <summary>GET /f/orders (CRM). Six orders for three contacts: a forEach over contactId calls the notes endpoint 3 times, not 6.</summary>
    public const string Orders = """
    {
      "orders": [
        { "id": "o-1001", "contactId": "c-101", "total": 42.50 },
        { "id": "o-1002", "contactId": "c-103", "total": 18.00 },
        { "id": "o-1003", "contactId": "c-101", "total": 7.25 },
        { "id": "o-1004", "contactId": "c-104", "total": 99.90 },
        { "id": "o-1005", "contactId": "c-101", "total": 12.00 },
        { "id": "o-1006", "contactId": "c-103", "total": 30.00 }
      ]
    }
    """;

    /// <summary>GET /f/contacts/{id}/notes (CRM). Unknown contacts get an empty list.</summary>
    public static string Notes(string contactId) => contactId switch
    {
        "c-101" => """{ "contactId": "c-101", "notes": [ { "date": "2026-04-02", "text": "Asked about the family plan." }, { "date": "2026-04-20", "text": "Prefers email." } ] }""",
        "c-103" => """{ "contactId": "c-103", "notes": [ { "date": "2026-04-15", "text": "Updated surname." } ] }""",
        "c-104" => """{ "contactId": "c-104", "notes": [ { "date": "2026-04-20", "text": "New work email." } ] }""",
        _ => $$"""{ "contactId": "{{contactId}}", "notes": [] }"""
    };

    /// <summary>The contacts that have notes (the sample orders only use these).</summary>
    public static readonly string[] ContactsWithNotes = ["c-101", "c-103", "c-104"];

    /// <summary>GET /g/accounts (Billing). Own account numbers and NO user id: they are linked through /h/links.</summary>
    public const string Accounts = """
    {
      "accounts": [
        { "accountNo": "ACC-1", "plan": "family",  "status": "active" },
        { "accountNo": "ACC-2", "plan": "single",  "status": "active" },
        { "accountNo": "ACC-3", "plan": "premium", "status": "overdue" },
        { "accountNo": "ACC-4", "plan": "single",  "status": "active" },
        { "accountNo": "ACC-5", "plan": "family",  "status": "closed" },
        { "accountNo": "ACC-6", "plan": "premium", "status": "active" }
      ]
    }
    """;

    /// <summary>
    /// GET /h/links (internal cross-reference). Billing account -> Identity user.
    ///   ACC-2 -> "U-2"        upper case: normalize applies to mapped ids too
    ///   ACC-4 -> "u-9"        links to the CRM-only person (Hugo)
    ///   ACC-5                 missing: unmapped
    ///   ACC-6 -> u-5 AND u-6  two different users: ambiguous, never merged
    /// </summary>
    public const string Links = """
    {
      "links": [
        { "billingAccountNo": "ACC-1", "userId": "u-1" },
        { "billingAccountNo": "ACC-2", "userId": "U-2" },
        { "billingAccountNo": "ACC-3", "userId": "u-4" },
        { "billingAccountNo": "ACC-4", "userId": "u-9" },
        { "billingAccountNo": "ACC-6", "userId": "u-5" },
        { "billingAccountNo": "ACC-6", "userId": "u-6" }
      ]
    }
    """;
}
