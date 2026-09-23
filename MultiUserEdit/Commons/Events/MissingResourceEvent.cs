namespace MultiUserEdit.Commons.Events
{
    public record MissingResourceEvent(string[] Fonts, string[] Plugins) : EditEvent;
}
