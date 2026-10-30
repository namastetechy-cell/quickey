using System.Reflection;
using System.Runtime.InteropServices;

namespace Quickey.App.Office;

/// <summary>
/// Late-bound calls through IDispatch by name. Unlike C# <c>dynamic</c>, which needs the object's
/// type information, this asks the object itself, so it also works on objects Word hands back
/// without usable type info (e.g. a document converted from PDF, where <c>dynamic</c> reported
/// "'System.__ComObject' does not contain a definition for 'SaveAs2'").
/// </summary>
internal static class Dispatch
{
    private const int UnknownName = unchecked((int)0x80020006); // DISP_E_UNKNOWNNAME

    public static object? Get(object target, string property) =>
        target.GetType().InvokeMember(property, BindingFlags.GetProperty, null, target, null);

    public static object? Call(object target, string method, params object?[] args) =>
        target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, args);

    /// <summary>Calls the first of <paramref name="methods"/> the object has (newest name first).</summary>
    public static object? CallFirst(object target, string[] methods, params object?[] args)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                return Call(target, methods[i], args);
            }
            catch (Exception ex) when (i < methods.Length - 1 && IsMissing(ex))
            {
                // Older Office: try the next name.
            }
        }
    }

    private static bool IsMissing(Exception ex) =>
        ex is MissingMethodException || (ex is COMException com && com.HResult == UnknownName)
        || (ex is TargetInvocationException { InnerException: { } inner } && IsMissing(inner));
}
