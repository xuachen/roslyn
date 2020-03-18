﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Composition;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.GenerateFromMembers;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.LanguageServices;
using Microsoft.CodeAnalysis.PickMembers;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.GenerateEqualsAndGetHashCodeFromMembers
{
    [ExportCodeRefactoringProvider(LanguageNames.CSharp, LanguageNames.VisualBasic,
        Name = PredefinedCodeRefactoringProviderNames.GenerateEqualsAndGetHashCodeFromMembers), Shared]
    [ExtensionOrder(After = PredefinedCodeRefactoringProviderNames.GenerateConstructorFromMembers,
                    Before = PredefinedCodeRefactoringProviderNames.AddConstructorParametersFromMembers)]
    internal partial class GenerateEqualsAndGetHashCodeFromMembersCodeRefactoringProvider : AbstractGenerateFromMembersCodeRefactoringProvider
    {
        public const string GenerateOperatorsId = nameof(GenerateOperatorsId);
        public const string ImplementIEquatableId = nameof(ImplementIEquatableId);

        private const string EqualsName = nameof(object.Equals);
        private const string GetHashCodeName = nameof(object.GetHashCode);

        private readonly IPickMembersService _pickMembersService_forTestingPurposes;

        [ImportingConstructor]
        public GenerateEqualsAndGetHashCodeFromMembersCodeRefactoringProvider()
            : this(pickMembersService: null)
        {
        }

        [SuppressMessage("RoslynDiagnosticsReliability", "RS0034:Exported parts should have [ImportingConstructor]", Justification = "Used incorrectly by tests")]
        public GenerateEqualsAndGetHashCodeFromMembersCodeRefactoringProvider(IPickMembersService pickMembersService)
        {
            _pickMembersService_forTestingPurposes = pickMembersService;
        }

        public override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            var (document, textSpan, cancellationToken) = context;
            if (document.Project.Solution.Workspace.Kind == WorkspaceKind.MiscellaneousFiles)
            {
                return;
            }

            var actions = await GenerateEqualsAndGetHashCodeFromMembersAsync(document, textSpan, cancellationToken).ConfigureAwait(false);
            context.RegisterRefactorings(actions);

            if (actions.IsDefaultOrEmpty && textSpan.IsEmpty)
            {
                await HandleNonSelectionAsync(context).ConfigureAwait(false);
            }
        }

        private async Task HandleNonSelectionAsync(CodeRefactoringContext context)
        {
            var (document, textSpan, cancellationToken) = context;

            var syntaxFacts = document.GetLanguageService<ISyntaxFactsService>();
            var sourceText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

            // We offer the refactoring when the user is either on the header of a class/struct,
            // or if they're between any members of a class/struct and are on a blank line.
            if (!syntaxFacts.IsOnTypeHeader(root, textSpan.Start, out var typeDeclaration) &&
                !syntaxFacts.IsBetweenTypeMembers(sourceText, root, textSpan.Start, out typeDeclaration))
            {
                return;
            }

            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);

            // Only supported on classes/structs.
            var containingType = semanticModel.GetDeclaredSymbol(typeDeclaration) as INamedTypeSymbol;
            if (containingType?.TypeKind != TypeKind.Class && containingType?.TypeKind != TypeKind.Struct)
            {
                return;
            }

            // No overrides in static classes.
            if (containingType.IsStatic)
            {
                return;
            }

            // Find all the possible instance fields/properties.  If there are any, then
            // show a dialog to the user to select the ones they want.
            var viableMembers = containingType
                .GetBaseTypesAndThis()
                .Reverse()
                .SelectAccessibleMembers<ISymbol>(containingType)
                .Where(IsReadableInstanceFieldOrProperty)
                .ToImmutableArray();

            if (viableMembers.Length == 0)
            {
                return;
            }

            GetExistingMemberInfo(
                containingType, out var hasEquals, out var hasGetHashCode);

            var actions = await CreateActionsAsync(
                document, textSpan, typeDeclaration, containingType, viableMembers,
                hasEquals, hasGetHashCode, withDialog: true, cancellationToken).ConfigureAwait(false);

            context.RegisterRefactorings(actions);
        }

        private bool HasOperators(INamedTypeSymbol containingType)
            => HasOperator(containingType, WellKnownMemberNames.EqualityOperatorName) ||
               HasOperator(containingType, WellKnownMemberNames.InequalityOperatorName);

        private bool HasOperator(INamedTypeSymbol containingType, string operatorName)
            => containingType.GetMembers(operatorName)
                             .OfType<IMethodSymbol>()
                             .Any(m => m.MethodKind == MethodKind.UserDefinedOperator &&
                                       m.Parameters.Length == 2 &&
                                       containingType.Equals(m.Parameters[0].Type) &&
                                       containingType.Equals(m.Parameters[1].Type));

        private bool CanImplementIEquatable(
            SemanticModel semanticModel, INamedTypeSymbol containingType,
            [NotNullWhen(true)] out INamedTypeSymbol constructedType)
        {
            var equatableTypeOpt = semanticModel.Compilation.GetTypeByMetadataName(typeof(IEquatable<>).FullName);
            if (equatableTypeOpt != null)
            {
                constructedType = equatableTypeOpt.Construct(containingType);
                return !containingType.AllInterfaces.Contains(constructedType);
            }

            constructedType = null;
            return false;
        }

        private void GetExistingMemberInfo(INamedTypeSymbol containingType, out bool hasEquals, out bool hasGetHashCode)
        {
            hasEquals = containingType.GetMembers(EqualsName)
                                      .OfType<IMethodSymbol>()
                                      .Any(m => m.Parameters.Length == 1 && !m.IsStatic);

            hasGetHashCode = containingType.GetMembers(GetHashCodeName)
                                           .OfType<IMethodSymbol>()
                                           .Any(m => m.Parameters.Length == 0 && !m.IsStatic);
        }

        public async Task<ImmutableArray<CodeAction>> GenerateEqualsAndGetHashCodeFromMembersAsync(
            Document document,
            TextSpan textSpan,
            CancellationToken cancellationToken)
        {
            using (Logger.LogBlock(FunctionId.Refactoring_GenerateFromMembers_GenerateEqualsAndGetHashCode, cancellationToken))
            {
                var info = await GetSelectedMemberInfoAsync(document, textSpan, allowPartialSelection: false, cancellationToken).ConfigureAwait(false);
                if (info != null &&
                    info.SelectedMembers.All(IsReadableInstanceFieldOrProperty))
                {
                    if (info.ContainingType != null && info.ContainingType.TypeKind != TypeKind.Interface)
                    {
                        GetExistingMemberInfo(
                            info.ContainingType, out var hasEquals, out var hasGetHashCode);

                        var syntaxFacts = document.GetLanguageService<ISyntaxFactsService>();
                        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                        var typeDeclaration = syntaxFacts.GetContainingTypeDeclaration(root, textSpan.Start);

                        return await CreateActionsAsync(
                            document, textSpan, typeDeclaration, info.ContainingType, info.SelectedMembers,
                            hasEquals, hasGetHashCode, withDialog: false, cancellationToken).ConfigureAwait(false);
                    }
                }

                return default;
            }
        }

        private async Task<ImmutableArray<CodeAction>> CreateActionsAsync(
            Document document, TextSpan textSpan, SyntaxNode typeDeclaration, INamedTypeSymbol containingType, ImmutableArray<ISymbol> selectedMembers,
            bool hasEquals, bool hasGetHashCode, bool withDialog, CancellationToken cancellationToken)
        {
            using var _ = ArrayBuilder<Task<CodeAction>>.GetInstance(out var tasks);

            if (!hasEquals && !hasGetHashCode)
            {
                // if we don't have either Equals or GetHashCode then offer:
                //  "Generate Equals" and
                //  "Generate Equals and GethashCode"
                //
                // Don't bother offering to just "Generate GetHashCode" as it's very unlikely
                // the user would need to bother just generating that member without also
                // generating 'Equals' as well.
                tasks.Add(CreateCodeActionAsync(
                    document, textSpan, typeDeclaration, containingType, selectedMembers,
                    generateEquals: true, generateGetHashCode: false, withDialog, cancellationToken));
                tasks.Add(CreateCodeActionAsync(
                    document, textSpan, typeDeclaration, containingType, selectedMembers,
                    generateEquals: true, generateGetHashCode: true, withDialog, cancellationToken));
            }
            else if (!hasEquals)
            {
                tasks.Add(CreateCodeActionAsync(
                    document, textSpan, typeDeclaration, containingType, selectedMembers,
                    generateEquals: true, generateGetHashCode: false, withDialog, cancellationToken));
            }
            else if (!hasGetHashCode)
            {
                tasks.Add(CreateCodeActionAsync(
                    document, textSpan, typeDeclaration, containingType, selectedMembers,
                    generateEquals: false, generateGetHashCode: true, withDialog, cancellationToken));
            }

            var codeActions = await Task.WhenAll(tasks).ConfigureAwait(false);
            return codeActions.ToImmutableArray();
        }

        private Task<CodeAction> CreateCodeActionAsync(
            Document document, TextSpan textSpan, SyntaxNode typeDeclaration, INamedTypeSymbol containingType, ImmutableArray<ISymbol> members,
            bool generateEquals, bool generateGetHashCode, bool withDialog, CancellationToken cancellationToken)
        {
            return withDialog
                ? CreateCodeActionWithDialogAsync(document, textSpan, typeDeclaration, containingType, members, generateEquals, generateGetHashCode, cancellationToken)
                : CreateCodeActionWithoutDialogAsync(document, textSpan, typeDeclaration, containingType, members, generateEquals, generateGetHashCode, cancellationToken);
        }

        private async Task<CodeAction> CreateCodeActionWithDialogAsync(
            Document document, TextSpan textSpan, SyntaxNode typeDeclaration, INamedTypeSymbol containingType, ImmutableArray<ISymbol> members,
            bool generateEquals, bool generateGetHashCode, CancellationToken cancellationToken)
        {
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var options = await document.GetOptionsAsync(cancellationToken).ConfigureAwait(false);

            using var _ = ArrayBuilder<PickMembersOption>.GetInstance(out var pickMembersOptions);

            var canImplementIEquatable = CanImplementIEquatable(semanticModel, containingType, out var equatableTypeOpt);
            var hasExistingOperators = HasOperators(containingType);

            if (canImplementIEquatable)
            {
                var value = options.GetOption(GenerateEqualsAndGetHashCodeFromMembersOptions.ImplementIEquatable);

                var displayName = equatableTypeOpt.ToDisplayString(new SymbolDisplayFormat(
                    typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
                    genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters));

                pickMembersOptions.Add(new PickMembersOption(
                    ImplementIEquatableId,
                    string.Format(FeaturesResources.Implement_0, displayName),
                    value));
            }

            if (!hasExistingOperators)
            {
                var value = options.GetOption(GenerateEqualsAndGetHashCodeFromMembersOptions.GenerateOperators);
                pickMembersOptions.Add(new PickMembersOption(
                    GenerateOperatorsId,
                    FeaturesResources.Generate_operators,
                    value));
            }

            return new GenerateEqualsAndGetHashCodeWithDialogCodeAction(
                this, document, textSpan, typeDeclaration, containingType, members,
                pickMembersOptions.ToImmutable(), generateEquals, generateGetHashCode);
        }

        private async Task<CodeAction> CreateCodeActionWithoutDialogAsync(
            Document document, TextSpan textSpan, SyntaxNode typeDeclaration, INamedTypeSymbol containingType, ImmutableArray<ISymbol> members,
            bool generateEquals, bool generateGetHashCode, CancellationToken cancellationToken)
        {
            var implementIEquatable = false;
            var generateOperators = false;

            if (generateEquals && containingType.TypeKind == TypeKind.Struct)
            {
                // if we're generating equals for a struct, then also add IEquatable<S> support as
                // well as operators (as long as the struct does not already have them).
                var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                implementIEquatable = CanImplementIEquatable(semanticModel, containingType, out var equatableTypeOpt);
                generateOperators = !HasOperators(containingType);
            }

            return new GenerateEqualsAndGetHashCodeAction(
                document, textSpan, typeDeclaration, containingType, members,
                generateEquals, generateGetHashCode, implementIEquatable, generateOperators);
        }
    }
}
