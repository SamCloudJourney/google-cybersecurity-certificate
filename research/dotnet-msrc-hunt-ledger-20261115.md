# .NET 10/11 MSRC boundary hunt — evidence checkpoint (2026-11-15)

## Status
**No new verified vulnerability.** Do **not** submit the Identity email-recipient behavior as a new MSRC issue.

## Exact released versions
- .NET / ASP.NET Core 10.0.15, SDK 10.0.404.
- .NET / ASP.NET Core 11.0.0, SDK 11.0.100.
- Test server uses `Microsoft.AspNetCore.Identity.EntityFrameworkCore` and `Microsoft.EntityFrameworkCore.InMemory` pinned to the release patch.
- Runner: Ubuntu GitHub Actions, synthetic `IdentityUser`, in-memory persistence, recording `IEmailSender<IdentityUser>` (no outbound email).

## Executed build and regression
Successful CI: https://github.com/SamCloudJourney/google-cybersecurity-certificate/actions/runs/43708173378
- Both matrix jobs passed with 0 warnings / 0 errors.
- `POST /identity/forgotPassword` case-insensitive match sent to the **stored** email address, not the differently cased request.
- `POST /identity/resendConfirmationEmail` case-insensitive match also sent to stored address.
- Dotless-i Unicode variant did **not** normalize to the stored address (negative control: no email).
- Unrelated address: no email.
- 6 endpoint/case checks per version, 12 successful assertions in the final run.

## Reason for investigating
- GitHub `dotnet/aspnetcore` release-tag sources at `v10.0.15` / `v11.0.0` showed request-supplied recipient in `src/Identity/Core/src/IdentityApiEndpointRouteBuilderExtensions.cs`.
- `main` source retrieves `UserManager.GetEmailAsync(user)`.
- The **actual shipped binaries in this lab** route to the stored address, so source-level discrepancy alone is not an exploitable claim.
- This root vulnerability family is already **CVE-2026-70175** (October 2026) with November 10 follow-up guidance.
- Public advisory: https://github.com/dotnet/aspnetcore/discussions/69793

## Supplemental source review (not executed as separate PoCs)
- `OpenIdConnectHandler`, `RemoteAuthenticationHandler`: sign-in callback checks Data Protection-protected state and correlation cookie before accepting a ticket; no independent bypass was demonstrated.
- `WsFederationHandler`: unsolicited login option is configurable; cryptographic token validation remains mandatory. No independent bypass shown.
- `TarEntry` (dotnet/runtime 10.0.15 / 11.0.0): extraction path code resolves archive-created symlinks and checks containment. It explicitly does not promise safety against symlinks pre-existing at the destination. No path traversal reproduced. The 10.0.15 branch includes a defensive empty-relative-path guard not present in the inspected 11.0.0 source; no security impact demonstrated.

## Next-candidate quality gate
To elevate *any* distinct lead: exact Microsoft-owned root cause; supported and default or realistic configuration; attacker-controlled input; crossed privilege boundary; repeatable behavior on the released runtime; negative controls; compare to already-advised CVEs and the reporter's existing submissions before reporting.
