namespace ResMan.Brain.Harness;

/// <summary>
/// GBNF grammars that constrain the local model's output. The model literally can't emit
/// malformed JSON, an unknown tool, or an action outside the allowed set.
/// </summary>
public static class Grammars
{
    private const string Common = """
        string ::= "\"" char* "\""
        char ::= [^"\\\x7F\x00-\x1F] | "\\" (["\\/bfnrt] | "u" [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F])
        bool ::= "true" | "false"
        ws ::= [ \t\n]{0,4}
        """;

    private const string Answer = """
        answer ::= "{" ws "\"action\"" ws ":" ws "\"answer\"" ws "," ws "\"summary\"" ws ":" ws string ws "," ws "\"likely_cause\"" ws ":" ws string ws "," ws "\"confidence\"" ws ":" ws conf ws "," ws "\"evidence\"" ws ":" ws list4 ws "," ws "\"steps\"" ws ":" ws list4 ws "," ws "\"needs_human\"" ws ":" ws bool ws "}"
        conf ::= "\"low\"" | "\"medium\"" | "\"high\""
        list4 ::= "[" ws ( string ( ws "," ws string ){0,3} )? ws "]"
        """;

    public static readonly string[] ToolNameList =
    [
        "get_health_events", "get_event_details", "get_resource_history", "get_changes", "get_network_history",
        "get_findings", "search_knowledge", "inspect_binary", "get_top_processes",
    ];

    public static string ToolNames => string.Join(", ", ToolNameList);

    private static string ToolRules => $$"""
        tool ::= "{" ws "\"action\"" ws ":" ws "\"tool\"" ws "," ws "\"tool\"" ws ":" ws toolname ws "," ws "\"input\"" ws ":" ws string ws "}"
        toolname ::= {{string.Join(" | ", ToolNameList.Select(t => $"\"\\\"{t}\\\"\""))}}
        """;

    /// <summary>First step: the model must investigate (call a tool) before it may answer.</summary>
    public static readonly string ToolOnly = $"""
        root ::= tool
        {ToolRules}
        {Common}
        """;

    public static readonly string ToolOrAnswer = $"""
        root ::= tool | answer
        {ToolRules}
        {Answer}
        {Common}
        """;

    public static readonly string AnswerOnly = $"""
        root ::= answer
        {Answer}
        {Common}
        """;

    public static readonly string Narration = $$"""
        root ::= "{" ws "\"narration\"" ws ":" ws string ws "," ws "\"key_moments\"" ws ":" ws moments ws "," ws "\"likely_cause\"" ws ":" ws string ws "}"
        moments ::= "[" ws ( moment ( ws "," ws moment ){0,4} )? ws "]"
        moment ::= "{" ws "\"t\"" ws ":" ws string ws "," ws "\"what\"" ws ":" ws string ws "}"
        {{Common}}
        """;

    public static readonly string Rule = $$"""
        root ::= rule | clarify
        rule ::= "{" ws "\"process\"" ws ":" ws procname ws "," ws "\"action\"" ws ":" ws action ws "," ws "\"value\"" ws ":" ws value ws "," ws "\"profile\"" ws ":" ws profile ws "," ws "\"reason\"" ws ":" ws string ws "}"
        clarify ::= "{" ws "\"clarify\"" ws ":" ws string ws "}"
        procname ::= "\"" [a-zA-Z0-9_. -]{1,40} "\""
        action ::= "\"set_priority\"" | "\"efficiency_mode\"" | "\"cpu_cap\"" | "\"memory_cap_mb\""
        value ::= "\"" [a-z0-9_%]{1,12} "\""
        profile ::= "\"any\"" | "\"gaming\"" | "\"focus\"" | "\"battery\"" | "\"idle\""
        {{Common}}
        """;
}
