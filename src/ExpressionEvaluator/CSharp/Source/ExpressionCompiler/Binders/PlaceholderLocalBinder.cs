// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Symbols.Metadata.PE;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.ExpressionEvaluator;
using Microsoft.VisualStudio.Debugger.Clr;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.ExpressionEvaluator
{
    internal sealed class PlaceholderLocalBinder : LocalScopeBinder
    {
        private readonly CSharpSyntaxNode _syntax;
        private readonly ImmutableArray<LocalSymbol> _aliases;
        private readonly MethodSymbol _containingMethod;
        private readonly ImmutableDictionary<string, LocalSymbol> _lowercaseReturnValueAliases;

        internal PlaceholderLocalBinder(
            CSharpSyntaxNode syntax,
            ImmutableArray<Alias> aliases,
            MethodSymbol containingMethod,
            EETypeNameDecoder typeNameDecoder,
            Binder next) :
            base(next)
        {
            _syntax = syntax;
            _containingMethod = containingMethod;

            var compilation = next.Compilation;
            var sourceAssembly = compilation.SourceAssembly;

            var aliasesBuilder = ArrayBuilder<LocalSymbol>.GetInstance(aliases.Length);
            var lowercaseBuilder = ImmutableDictionary.CreateBuilder<string, LocalSymbol>();
            foreach (Alias alias in aliases)
            {
                var local = PlaceholderLocalSymbol.Create(
                    typeNameDecoder,
                    containingMethod,
                    sourceAssembly,
                    alias);
                aliasesBuilder.Add(local);

                if (alias.Kind == DkmClrAliasKind.ReturnValue)
                {
                    lowercaseBuilder.Add(local.Name.ToLower(), local);
                }
            }
            _lowercaseReturnValueAliases = lowercaseBuilder.ToImmutableDictionary();
            _aliases = aliasesBuilder.ToImmutableAndFree();
        }

        internal sealed override void LookupSymbolsInSingleBinder(
            LookupResult result,
            string name,
            int arity,
            ConsList<Symbol> basesBeingResolved,
            LookupOptions options,
            Binder originalBinder,
            bool diagnose,
            ref HashSet<DiagnosticInfo> useSiteDiagnostics)
        {
            if ((options & (LookupOptions.NamespaceAliasesOnly | LookupOptions.NamespacesOrTypesOnly | LookupOptions.LabelsOnly)) != 0)
            {
                return;
            }

            if (name.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                var valueText = name.Substring(2);
                ulong address;
                if (!ulong.TryParse(valueText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out address))
                {
                    // Invalid value should have been caught by Lexer.
                    throw ExceptionUtilities.UnexpectedValue(valueText);
                }
                var local = new ObjectAddressLocalSymbol(_containingMethod, name, this.Compilation.GetSpecialType(SpecialType.System_Object), address);
                result.MergeEqual(this.CheckViability(local, arity, options, null, diagnose, ref useSiteDiagnostics, basesBeingResolved));
            }
            else
            {
                LocalSymbol lowercaseReturnValueAlias;
                if (_lowercaseReturnValueAliases.TryGetValue(name, out lowercaseReturnValueAlias))
                {
                    result.MergeEqual(this.CheckViability(lowercaseReturnValueAlias, arity, options, null, diagnose, ref useSiteDiagnostics, basesBeingResolved));
                }
                else
                {
                    base.LookupSymbolsInSingleBinder(result, name, arity, basesBeingResolved, options, originalBinder, diagnose, ref useSiteDiagnostics);
                }
            }
        }

        protected sealed override void AddLookupSymbolsInfoInSingleBinder(LookupSymbolsInfo info, LookupOptions options, Binder originalBinder)
        {
            throw new NotImplementedException();
        }

        protected override ImmutableArray<LocalSymbol> BuildLocals()
        {
            var builder = ArrayBuilder<LocalSymbol>.GetInstance();
            builder.AddRange(_aliases);
            var declaration = _syntax as LocalDeclarationStatementSyntax;
            if (declaration != null)
            {
                var kind = declaration.IsConst ? LocalDeclarationKind.Constant : LocalDeclarationKind.RegularVariable;
                foreach (var variable in declaration.Declaration.Variables)
                {
                    var local = SourceLocalSymbol.MakeLocal(
                        _containingMethod, 
                        this, 
                        declaration.RefKeyword.Kind() == SyntaxKind.RefKeyword? RefKind.Ref: RefKind.None,
                        declaration.Declaration.Type, 
                        variable.Identifier, 
                        kind, 
                        variable.Initializer);
                    builder.Add(local);
                }
            }
            return builder.ToImmutableAndFree();
        }
    }
}
