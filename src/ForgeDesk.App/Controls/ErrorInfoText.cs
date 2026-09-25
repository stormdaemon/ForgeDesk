using System.Text;
using ForgeDesk.Core.Common;

namespace ForgeDesk.App.Controls;

/// <summary>Plain-text rendering of an error, for "Copy details" and bug reports.</summary>
internal static class ErrorInfoText
{
    public static string Format(ErrorInfo error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var builder = new StringBuilder();
        builder.AppendLine(error.Title);
        builder.AppendLine(error.Message);
        if (!string.IsNullOrWhiteSpace(error.Hint))
        {
            builder.AppendLine(error.Hint);
        }

        builder.AppendLine();
        builder.Append("Kind: ").AppendLine(error.Kind.ToString());
        if (!string.IsNullOrWhiteSpace(error.Detail))
        {
            builder.AppendLine();
            builder.AppendLine(error.Detail.TrimEnd());
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }
}
