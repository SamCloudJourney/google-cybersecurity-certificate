using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

const string storedEmail = "Victim.Case@lab.example";
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ProbeDb>(options => options.UseInMemoryDatabase("isolated-identity-lab"));
builder.Services.AddAuthorization();
builder.Services.AddIdentityApiEndpoints<IdentityUser>()
    .AddEntityFrameworkStores<ProbeDb>();
builder.Services.AddSingleton<RecordingEmailSender>();
builder.Services.AddSingleton<IEmailSender<IdentityUser>>(sp => sp.GetRequiredService<RecordingEmailSender>());

var app = builder.Build();
app.MapGroup("/identity").MapIdentityApi<IdentityUser>();

// Synthetic fixture: no external accounts, email delivery, or sensitive tokens.
app.MapPost("/_lab/seed", async (UserManager<IdentityUser> manager) =>
{
    var user = new IdentityUser
    {
        UserName = storedEmail,
        Email = storedEmail,
        EmailConfirmed = true
    };
    var result = await manager.CreateAsync(user, "LocalOnly-Password!2026");
    return Results.Json(new
    {
        success = result.Succeeded,
        errors = result.Errors.Select(x => x.Code).ToArray()
    });
});

app.MapGet("/_lab/email-result", (RecordingEmailSender sender) =>
    Results.Json(new { sender.Kind, sender.Recipient, expected = storedEmail }));

app.MapPost("/_lab/reset-emails", (RecordingEmailSender sender) => { sender.Clear(); return Results.Ok(); });

app.MapGet("/_lab/normalize", (ILookupNormalizer normalizer) => Results.Json(new
{
    stored = normalizer.NormalizeEmail(storedEmail),
    ascii = normalizer.NormalizeEmail("victim.case@lab.example"),
    dotless = normalizer.NormalizeEmail("V\u0131ctim.Case@lab.example"),
    unrelated = normalizer.NormalizeEmail("outsider@lab.example")
}));

app.Run();

sealed class ProbeDb(DbContextOptions<ProbeDb> options) : IdentityDbContext<IdentityUser>(options) { }

sealed class RecordingEmailSender : IEmailSender<IdentityUser>
{
    public string? Kind { get; private set; }
    public string? Recipient { get; private set; }
    public void Clear() { Kind = null; Recipient = null; }

    public Task SendConfirmationLinkAsync(IdentityUser user, string email, string confirmationLink)
    {
        Kind = "confirmation";
        Recipient = email;
        return Task.CompletedTask;
    }

    public Task SendPasswordResetLinkAsync(IdentityUser user, string email, string resetLink)
    {
        Kind = "reset-link";
        Recipient = email;
        return Task.CompletedTask;
    }

    public Task SendPasswordResetCodeAsync(IdentityUser user, string email, string resetCode)
    {
        Kind = "reset-code";
        Recipient = email;
        return Task.CompletedTask;
    }
}
