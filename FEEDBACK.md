# Feedback Ledger

Tracks changes made in response to lecturer feedback, as required for the final PoE.

## After Part 1 (mark: 90%)

| # | Area | Change | Files |
|---|---|---|---|
| 1 | Security | File uploads strip directory parts with `Path.GetFileName` to block path traversal. | `SmartX.Api/Program.cs` |
| 2 | Validation | Client-side MAC address validation with clear error messages. | `SmartX.Client/Pages/Ingestion.razor` |
| 3 | Robustness | Ingestion page API calls wrapped in error handling; the UI survives an API outage. | `SmartX.Client/Pages/Ingestion.razor` |
| 4 | Configuration | API address moved from hard-coded C# to `wwwroot/appsettings.json`. | `SmartX.Client/Program.cs` |
| 5 | Integration | Part 1 sensors join the Part 2 live registry and stream. | `SmartX.Api/Program.cs` |
| 6 | Referencing | Report references use clean official URLs in consistent IEEE style. | Part 2 document |

## After Part 2

_To be completed when Part 2 feedback is received._