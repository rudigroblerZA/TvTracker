using Identity.API.Services;
using Microsoft.Extensions.Configuration;

namespace Identity.UnitTests;

/// <summary>
/// The mail account Keycloak is given, as configuration describes it.
/// </summary>
public class MailAccountTests
{
    private static MailAccount? Read(params (string Key, string? Value)[] settings) =>
        MailAccount.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>($"Keycloak:Smtp:{s.Key}", s.Value))).Build(), "Couch Club");

    [Fact]
    public void Without_a_host_or_a_from_address_there_is_no_account()
    {
        Assert.Null(Read());
        Assert.Null(Read(("Host", "smtp.example.org")));
        Assert.Null(Read(("Host", "  "), ("From", "hello@example.org")));
    }

    [Fact]
    public void An_account_defaults_to_port_587_with_STARTTLS_and_the_apps_name()
    {
        var mail = Read(("Host", " smtp.example.org "), ("From", "hello@example.org"), ("Port", ""), ("User", "apikey"), ("Password", "secret"));

        Assert.Equal(new MailAccount("smtp.example.org", 587, "hello@example.org", "Couch Club", "apikey", "secret", MailEncryption.StartTls), mail);
    }

    [Theory]
    [InlineData("465", null, MailEncryption.Ssl)]
    [InlineData("587", null, MailEncryption.StartTls)]
    [InlineData("2525", null, MailEncryption.StartTls)]
    [InlineData("1025", null, MailEncryption.None)]
    [InlineData("587", "ssl", MailEncryption.Ssl)]
    [InlineData("465", "none", MailEncryption.None)]
    [InlineData("not a port", null, MailEncryption.StartTls)]
    public void Encryption_follows_the_port_unless_said(string port, string? encryption, MailEncryption expected) =>
        Assert.Equal(expected, Read(("Host", "smtp.example.org"), ("From", "hello@example.org"), ("Port", port), ("Encryption", encryption))!.Encryption);

    [Theory]
    [InlineData("25")]
    [InlineData("1025")]
    public void A_sign_in_is_never_sent_in_the_clear_unless_asked_for(string port)
    {
        Assert.Equal(MailEncryption.StartTls, Read(("Host", "smtp.example.org"), ("From", "a@example.org"), ("Port", port), ("User", "apikey"))!.Encryption);
        Assert.Equal(MailEncryption.None, Read(("Host", "smtp.example.org"), ("From", "a@example.org"), ("Port", port))!.Encryption);
        Assert.Equal(MailEncryption.None,
            Read(("Host", "smtp.example.org"), ("From", "a@example.org"), ("Port", port), ("User", "apikey"), ("Encryption", "none"))!.Encryption);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("true", true)]
    [InlineData("flase", true)]
    [InlineData("false", false)]
    [InlineData(" No ", false)]
    [InlineData("off", false)]
    [InlineData("0", false)]
    public void New_accounts_confirm_their_address_unless_told_not_to(string? setting, bool expected) =>
        Assert.Equal(expected, MailAccount.VerifiesAddresses(new ConfigurationBuilder()
            .AddInMemoryCollection([new("Keycloak:Smtp:VerifyEmail", setting)]).Build()));

    [Fact]
    public void Mail_comes_from_the_name_in_settings_then_the_configured_one()
    {
        var configured = new ConfigurationBuilder().AddInMemoryCollection([new("Branding:AppName", "Couch Club")]).Build();

        Assert.Equal("Sofa Society", MailAccount.AppName(configured, " Sofa Society "));
        Assert.Equal("Couch Club", MailAccount.AppName(configured, null));
        Assert.Equal("TV Tracker", MailAccount.AppName(new ConfigurationBuilder().Build(), " "));
    }

    [Fact]
    public void A_server_that_asks_for_no_sign_in_has_no_user()
    {
        var mail = Read(("Host", "localhost"), ("From", "hello@example.org"), ("Port", "1025"), ("User", ""));

        Assert.Null(mail!.User);
        Assert.Null(mail.Password);
    }
}
