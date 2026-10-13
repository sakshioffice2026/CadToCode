using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CadToCode.Business.Sandbox;

public sealed class SandboxScriptRewriter : CSharpSyntaxRewriter
{
    private const string TickCall = "global::CadToCode.Business.Sandbox.SandboxGuard.Tick();";
    private const string ConsoleType = "global::CadToCode.Business.Sandbox.SandboxConsole";
    private const string PathType = "global::CadToCode.Business.Sandbox.SandboxPath";

    private readonly HashSet<int> _consoleSpans;
    private readonly HashSet<int> _pathSpans;

    private SandboxScriptRewriter(HashSet<int> consoleSpans, HashSet<int> pathSpans)
    {
        _consoleSpans = consoleSpans;
        _pathSpans = pathSpans;
    }

    public static string Rewrite(SyntaxTree tree, SandboxValidation validation)
    {
        var rewriter = new SandboxScriptRewriter(validation.ConsoleSpans, validation.PathSpans);
        var root = rewriter.Visit(tree.GetRoot());

        return root.ToFullString();
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        if (_consoleSpans.Contains(node.SpanStart))
        {
            return SyntaxFactory.ParseExpression(ConsoleType).WithTriviaFrom(node);
        }

        if (_pathSpans.Contains(node.SpanStart))
        {
            return SyntaxFactory.ParseExpression(PathType).WithTriviaFrom(node);
        }

        return base.VisitIdentifierName(node);
    }

    public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
    {
        var visited = (ForStatementSyntax)base.VisitForStatement(node)!;
        return visited.WithStatement(Guard(visited.Statement));
    }

    public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
    {
        var visited = (ForEachStatementSyntax)base.VisitForEachStatement(node)!;
        return visited.WithStatement(Guard(visited.Statement));
    }

    public override SyntaxNode? VisitForEachVariableStatement(ForEachVariableStatementSyntax node)
    {
        var visited = (ForEachVariableStatementSyntax)base.VisitForEachVariableStatement(node)!;
        return visited.WithStatement(Guard(visited.Statement));
    }

    public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
    {
        var visited = (WhileStatementSyntax)base.VisitWhileStatement(node)!;
        return visited.WithStatement(Guard(visited.Statement));
    }

    public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
    {
        var visited = (DoStatementSyntax)base.VisitDoStatement(node)!;
        return visited.WithStatement(Guard(visited.Statement));
    }

    private static StatementSyntax Guard(StatementSyntax body)
    {
        var tick = SyntaxFactory.ParseStatement(TickCall);

        if (body is BlockSyntax block)
        {
            return block.WithStatements(block.Statements.Insert(0, tick));
        }

        return SyntaxFactory.Block(tick, body);
    }
}