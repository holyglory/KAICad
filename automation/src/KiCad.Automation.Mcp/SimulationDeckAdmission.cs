using KiCad.Automation.Model;

namespace KiCad.Automation.Mcp;

// Caller-supplied ngspice decks are design or document text, never host instructions
// (security assumption SA-05). Reject the directives that make ngspice run interpreter
// commands (which include `shell`) or read other files. Decks KiCad generates from the
// native schematic (an empty netlist) are not affected. ngspice matches dot commands
// by case-insensitive prefix and joins '+' continuation lines, so admission does too.
internal static class SimulationDeckAdmission
{
    private const int MaximumLength = 1 << 20;
    private static readonly string[] CommandDirectives = [".control", ".endc", ".exec"];

    public static void Validate(string netlist)
    {
        if (string.IsNullOrEmpty(netlist)) return;
        if (netlist.Length > MaximumLength)
            Reject("The simulation netlist exceeds 1 MiB.");
        if (netlist.Any(character => char.IsControl(character) && character is not '\n' and not '\r' and not '\t'))
            Reject("The simulation netlist contains control characters.");

        var lines = new List<string>();
        foreach (string raw in netlist.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.TrimStart(' ', '\t');
            if (line.StartsWith('+') && lines.Count > 0) lines[^1] += " " + line[1..];
            else lines.Add(line);
        }
        foreach (string line in lines)
        {
            if (line.StartsWith("*ng_script", StringComparison.OrdinalIgnoreCase))
                Reject("ngspice script decks run interpreter commands; supply a circuit netlist.");
            string[] tokens = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;
            string directive = tokens[0];
            if (CommandDirectives.Any(command => directive.StartsWith(command, StringComparison.OrdinalIgnoreCase)))
                Reject("Interpreter command blocks (.control/.exec) are not accepted in caller-supplied netlists.");
            if (directive.StartsWith(".inc", StringComparison.OrdinalIgnoreCase))
                Reject("File includes are not accepted in caller-supplied netlists; inline the models.");
            // `.lib <file> <section>` reads a file; `.lib <section>` ... `.endl` is inline.
            if (directive.StartsWith(".lib", StringComparison.OrdinalIgnoreCase) && tokens.Length > 2)
                Reject("Library file references are not accepted in caller-supplied netlists; inline the models.");
        }
    }

    private static void Reject(string message) => throw new AutomationException("simulation_deck_rejected", message);
}
