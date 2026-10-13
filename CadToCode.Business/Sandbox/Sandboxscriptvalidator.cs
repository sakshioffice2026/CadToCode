using CadToCode.Models.Dtos;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CadToCode.Business.Sandbox;

public sealed class SandboxValidation
{
    public List<SandboxDiagnostic> Errors { get; } = new();

    public HashSet<int> ConsoleSpans { get; } = new();

    public HashSet<int> PathSpans { get; } = new();

    public bool IsValid => Errors.Count == 0;
}

public static class SandboxScriptValidator
{
    private const int MaxErrors = 25;
    private const int MaxArrayLength = 100000;
    private const string StepNamespacePrefix = "CadToCode.Step.";

    private static readonly SymbolDisplayFormat KeyFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.None,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.ExpandValueTuple);

    private static readonly HashSet<string> AllowedNamespaces = new(StringComparer.Ordinal)
    {
        "System",
        "System.Collections",
        "System.Collections.Generic",
        "System.IO",
        "System.Linq",
        "CadToCode",
        "CadToCode.Step"
    };

    private static readonly HashSet<string> AllowedUsings = new(StringComparer.Ordinal)
    {
        "System",
        "System.Collections.Generic",
        "System.IO",
        "System.Linq",
        "CadToCode.Step"
    };

    private static readonly HashSet<string> ObjectMembers = Set("ToString", "Equals", "GetHashCode");

    private static readonly Dictionary<string, HashSet<string>?> AllowedTypes = new(StringComparer.Ordinal)
    {
        ["System.Object"] = ObjectMembers,
        ["System.ValueType"] = ObjectMembers,
        ["System.Enum"] = ObjectMembers,
        ["System.Double"] = null,
        ["System.Single"] = null,
        ["System.Int32"] = null,
        ["System.Int64"] = null,
        ["System.Int16"] = null,
        ["System.Byte"] = null,
        ["System.SByte"] = null,
        ["System.UInt16"] = null,
        ["System.UInt32"] = null,
        ["System.UInt64"] = null,
        ["System.Boolean"] = null,
        ["System.Char"] = null,
        ["System.Decimal"] = null,
        ["System.Math"] = null,
        ["System.ValueTuple"] = null,
        ["System.Collections.IEnumerable"] = null,
        ["System.Collections.IEnumerator"] = null,
        ["System.Collections.Generic.IEnumerable"] = null,
        ["System.Collections.Generic.IEnumerator"] = null,
        ["System.Collections.Generic.IList"] = null,
        ["System.Collections.Generic.ICollection"] = null,
        ["System.Collections.Generic.IReadOnlyList"] = null,
        ["System.Array"] = Set("Length"),
        ["System.String"] = Set(
            "Length", "Contains", "StartsWith", "EndsWith", "IndexOf", "Substring", "Trim", "TrimStart", "TrimEnd",
            "ToUpper", "ToLower", "ToUpperInvariant", "ToLowerInvariant", "Join", "Concat", "IsNullOrEmpty",
            "IsNullOrWhiteSpace", "Equals", "CompareTo", "ToString", "GetHashCode", "Split", "Empty", "Item", "this[]"),
        ["System.Collections.Generic.List"] = Set(
            ".ctor", "Add", "Count", "Contains", "Clear", "Insert", "RemoveAt", "IndexOf", "Reverse", "ToArray",
            "GetEnumerator", "Item", "this[]"),
        ["System.Linq.Enumerable"] = Set(
            "Select", "Where", "ToList", "ToArray", "Any", "All", "Count", "Sum", "Min", "Max", "Average",
            "First", "FirstOrDefault", "Last", "LastOrDefault", "OrderBy", "OrderByDescending", "ThenBy",
            "ThenByDescending", "Reverse", "Take", "Skip", "Concat", "Distinct", "Contains"),
        ["System.Console"] = Set("WriteLine"),
        ["System.IO.Path"] = Set("GetFullPath")
    };

    public static SandboxValidation Validate(Compilation compilation, SyntaxTree tree)
    {
        var result = new SandboxValidation();
        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();

        CheckDirectivesAndTokens(root, result);
        CheckSyntaxKinds(root, model, result);
        CheckUsings(root, model, result);
        CheckNames(root, model, result);
        CheckCreationsAndIndexers(root, model, result);
        CheckRecursion(root, model, result);

        return result;
    }

    private static void CheckDirectivesAndTokens(SyntaxNode root, SandboxValidation result)
    {
        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: false))
        {
            if (trivia.IsDirective)
            {
                AddError(result, trivia.GetLocation(), "Preprocessor directives are not allowed.");
            }
        }

        foreach (var token in root.DescendantTokens())
        {
            switch (token.Kind())
            {
                case SyntaxKind.AsyncKeyword:
                case SyntaxKind.UnsafeKeyword:
                case SyntaxKind.ExternKeyword:
                case SyntaxKind.StackAllocKeyword:
                case SyntaxKind.FixedKeyword:
                    AddError(result, token.GetLocation(), "The keyword '" + token.Text + "' is not allowed.");
                    break;
            }
        }
    }

    private static void CheckSyntaxKinds(SyntaxNode root, SemanticModel model, SandboxValidation result)
    {
        foreach (var node in root.DescendantNodes())
        {
            string? message = node switch
            {
                BaseTypeDeclarationSyntax => "Type declarations are not allowed.",
                DelegateDeclarationSyntax => "Delegate declarations are not allowed.",
                BaseNamespaceDeclarationSyntax => "Namespace declarations are not allowed.",
                AttributeListSyntax => "Attributes are not allowed.",
                TryStatementSyntax => "try/catch/finally is not allowed.",
                UsingStatementSyntax => "using statements are not allowed.",
                LockStatementSyntax => "lock statements are not allowed.",
                FixedStatementSyntax => "fixed statements are not allowed.",
                UnsafeStatementSyntax => "unsafe code is not allowed.",
                GotoStatementSyntax => "goto is not allowed.",
                LabeledStatementSyntax => "Labels are not allowed.",
                YieldStatementSyntax => "yield is not allowed.",
                AwaitExpressionSyntax => "await is not allowed.",
                TypeOfExpressionSyntax => "typeof is not allowed.",
                SizeOfExpressionSyntax => "sizeof is not allowed.",
                StackAllocArrayCreationExpressionSyntax => "stackalloc is not allowed.",
                ImplicitStackAllocArrayCreationExpressionSyntax => "stackalloc is not allowed.",
                AnonymousObjectCreationExpressionSyntax => "Anonymous objects are not allowed.",
                PointerTypeSyntax => "Pointer types are not allowed.",
                FunctionPointerTypeSyntax => "Function pointers are not allowed.",
                QueryExpressionSyntax => "Query expressions are not allowed. Use Select and Where instead.",
                InterpolationAlignmentClauseSyntax => "Alignment in interpolated strings is not allowed.",
                _ => null
            };

            if (message != null)
            {
                AddError(result, node.GetLocation(), message);
            }
        }
    }

    private static void CheckUsings(SyntaxNode root, SemanticModel model, SandboxValidation result)
    {
        foreach (var directive in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            if (directive.Alias != null || directive.StaticKeyword != default || directive.Name == null)
            {
                AddError(result, directive.GetLocation(), "Only plain using directives are allowed.");
                continue;
            }

            var name = directive.Name.ToString().Replace(" ", string.Empty);
            if (!AllowedUsings.Contains(name))
            {
                AddError(result, directive.GetLocation(), "The namespace '" + name + "' is not allowed.");
            }
        }
    }

    private static void CheckNames(SyntaxNode root, SemanticModel model, SandboxValidation result)
    {
        foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            if (result.Errors.Count >= MaxErrors)
            {
                return;
            }

            if (name.Ancestors().OfType<UsingDirectiveSyntax>().Any())
            {
                continue;
            }

            if (name is IdentifierNameSyntax { IsVar: true })
            {
                continue;
            }

            var info = model.GetSymbolInfo(name);
            var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();

            if (symbol is null)
            {
                continue;
            }

            var message = Reject(symbol);
            if (message != null)
            {
                AddError(result, name.GetLocation(), message);
                continue;
            }

            if (symbol is INamedTypeSymbol named)
            {
                var key = TypeKey(named);
                var isConsole = key == "System.Console";
                var isPath = key == "System.IO.Path";

                if (!isConsole && !isPath)
                {
                    continue;
                }

                if (name.Parent is MemberAccessExpressionSyntax access && access.Expression == name)
                {
                    (isConsole ? result.ConsoleSpans : result.PathSpans).Add(name.SpanStart);
                }
                else
                {
                    AddError(result, name.GetLocation(), "Use Console.WriteLine and Path.GetFullPath directly, without a namespace prefix.");
                }
            }
        }
    }

    private static void CheckCreationsAndIndexers(SyntaxNode root, SemanticModel model, SandboxValidation result)
    {
        foreach (var node in root.DescendantNodes())
        {
            if (result.Errors.Count >= MaxErrors)
            {
                return;
            }

            switch (node)
            {
                case ObjectCreationExpressionSyntax:
                case ImplicitObjectCreationExpressionSyntax:
                case ElementAccessExpressionSyntax:
                    {
                        var symbol = model.GetSymbolInfo(node).Symbol;
                        if (symbol is null)
                        {
                            break;
                        }

                        var message = Reject(symbol);
                        if (message != null)
                        {
                            AddError(result, node.GetLocation(), message);
                        }

                        break;
                    }

                case ArrayCreationExpressionSyntax array when array.Initializer is null:
                    {
                        foreach (var rank in array.Type.RankSpecifiers)
                        {
                            foreach (var size in rank.Sizes)
                            {
                                if (size is OmittedArraySizeExpressionSyntax)
                                {
                                    continue;
                                }

                                var constant = model.GetConstantValue(size);
                                if (!constant.HasValue || constant.Value is not int length || length < 0 || length > MaxArrayLength)
                                {
                                    AddError(result, size.GetLocation(), "Array sizes must be constant numbers up to " + MaxArrayLength + ".");
                                }
                            }
                        }

                        break;
                    }
            }
        }
    }

    private static void CheckRecursion(SyntaxNode root, SemanticModel model, SandboxValidation result)
    {
        var functions = new Dictionary<ISymbol, LocalFunctionStatementSyntax>(SymbolEqualityComparer.Default);

        foreach (var function in root.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
        {
            var symbol = model.GetDeclaredSymbol(function);
            if (symbol != null)
            {
                functions[symbol.OriginalDefinition] = function;
            }
        }

        if (functions.Count == 0)
        {
            return;
        }

        var edges = new Dictionary<ISymbol, HashSet<ISymbol>>(SymbolEqualityComparer.Default);

        foreach (var pair in functions)
        {
            var targets = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

            foreach (var identifier in pair.Value.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                var target = model.GetSymbolInfo(identifier).Symbol?.OriginalDefinition;
                if (target != null && functions.ContainsKey(target))
                {
                    targets.Add(target);
                }
            }

            edges[pair.Key] = targets;
        }

        var state = new Dictionary<ISymbol, int>(SymbolEqualityComparer.Default);

        foreach (var function in functions.Keys)
        {
            if (HasCycle(function, edges, state))
            {
                AddError(result, functions[function].GetLocation(), "Recursive local functions are not allowed.");
                return;
            }
        }
    }

    private static bool HasCycle(
        ISymbol node,
        Dictionary<ISymbol, HashSet<ISymbol>> edges,
        Dictionary<ISymbol, int> state)
    {
        if (state.TryGetValue(node, out var value))
        {
            return value == 1;
        }

        state[node] = 1;

        foreach (var next in edges[node])
        {
            if (HasCycle(next, edges, state))
            {
                return true;
            }
        }

        state[node] = 2;
        return false;
    }

    private static string? Reject(ISymbol symbol)
    {
        if (symbol.Locations.Any(l => l.IsInSource))
        {
            return null;
        }

        switch (symbol.Kind)
        {
            case SymbolKind.Local:
            case SymbolKind.Parameter:
            case SymbolKind.RangeVariable:
            case SymbolKind.TypeParameter:
            case SymbolKind.Label:
            case SymbolKind.Discard:
                return null;

            case SymbolKind.Namespace:
                var ns = (INamespaceSymbol)symbol;
                return ns.IsGlobalNamespace || AllowedNamespaces.Contains(ns.ToDisplayString())
                    ? null
                    : "The namespace '" + ns.ToDisplayString() + "' is not allowed.";

            case SymbolKind.NamedType:
            case SymbolKind.ArrayType:
            case SymbolKind.PointerType:
            case SymbolKind.FunctionPointerType:
            case SymbolKind.DynamicType:
            case SymbolKind.ErrorType:
                return RejectType((ITypeSymbol)symbol);

            case SymbolKind.Method:
            case SymbolKind.Property:
            case SymbolKind.Field:
            case SymbolKind.Event:
                return RejectMember(symbol);

            default:
                return "The symbol '" + symbol.Name + "' is not allowed.";
        }
    }

    private static string? RejectType(ITypeSymbol type)
    {
        switch (type.TypeKind)
        {
            case TypeKind.Array:
                return RejectType(((IArrayTypeSymbol)type).ElementType);

            case TypeKind.TypeParameter:
                return null;

            case TypeKind.Dynamic:
            case TypeKind.Pointer:
            case TypeKind.FunctionPointer:
            case TypeKind.Error:
                return "The type '" + type.Name + "' is not allowed.";
        }

        if (type.IsAnonymousType)
        {
            return "Anonymous types are not allowed.";
        }

        if (type.Locations.Any(l => l.IsInSource))
        {
            return null;
        }

        var key = TypeKey(type);

        if (AllowedTypes.ContainsKey(key) || key.StartsWith(StepNamespacePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        return "The type '" + key + "' is not allowed in the sandbox.";
    }

    private static string? RejectMember(ISymbol member)
    {
        var method = member as IMethodSymbol;
        var effective = method is null ? member : method.ReducedFrom ?? method;
        var type = effective.ContainingType;

        if (type is null)
        {
            return "The member '" + member.Name + "' is not allowed.";
        }

        var key = TypeKey(type);

        if (key.StartsWith(StepNamespacePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        if (!AllowedTypes.TryGetValue(key, out var members))
        {
            return "The type '" + key + "' is not allowed in the sandbox.";
        }

        if (members != null && !members.Contains(effective.Name))
        {
            return "The member '" + key + "." + effective.Name + "' is not allowed in the sandbox.";
        }

        if (effective.Name == "ToString" && method is { Parameters.Length: > 0 })
        {
            return "ToString with arguments is not allowed in the sandbox.";
        }

        if (method is { MethodKind: MethodKind.Constructor }
            && key == "System.Collections.Generic.List"
            && method.Parameters.Any(p => p.Type.SpecialType == SpecialType.System_Int32))
        {
            return "Creating a List with a preset capacity is not allowed in the sandbox.";
        }

        return null;
    }

    private static string TypeKey(ITypeSymbol type)
    {
        return type.OriginalDefinition.ToDisplayString(KeyFormat);
    }

    private static void AddError(SandboxValidation result, Location location, string message)
    {
        if (result.Errors.Count >= MaxErrors)
        {
            return;
        }

        var position = location.GetLineSpan().StartLinePosition;

        result.Errors.Add(new SandboxDiagnostic
        {
            Severity = "error",
            Message = message,
            Line = position.Line + 1,
            Column = position.Character + 1
        });
    }

    private static HashSet<string> Set(params string[] values)
    {
        return new HashSet<string>(values, StringComparer.Ordinal);
    }
}