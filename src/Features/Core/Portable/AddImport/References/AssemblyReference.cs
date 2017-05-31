﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.SymbolSearch;
using Microsoft.CodeAnalysis.Tags;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CodeFixes.AddImport
{
    internal abstract partial class AbstractAddImportCodeFixProvider<TSimpleNameSyntax>
    {
        private class AssemblyReference : Reference
        {
            private readonly ReferenceAssemblyWithTypeResult _referenceAssemblyWithType;

            public AssemblyReference(
                AbstractAddImportCodeFixProvider<TSimpleNameSyntax> provider,
                SearchResult searchResult,
                ReferenceAssemblyWithTypeResult referenceAssemblyWithType)
                : base(provider, searchResult)
            {
                _referenceAssemblyWithType = referenceAssemblyWithType;
            }

            public override async Task<CodeAction> CreateCodeActionAsync(
                Document document, SyntaxNode node, bool placeSystemNamespaceFirst, CancellationToken cancellationToken)
            {
                var originalDocument = document;

                // First add the "using/import" directive in the code.
                (node, document) = await this.ReplaceNameNodeAsync(
                    node, document, cancellationToken).ConfigureAwait(false);

                var newDocument = await this.provider.AddImportAsync(
                    node, this.SearchResult.NameParts, document, placeSystemNamespaceFirst, cancellationToken).ConfigureAwait(false);


                return new AssemblyReferenceCodeAction(
                    this, originalDocument, newDocument, placeSystemNamespaceFirst);
            }

            public override bool Equals(object obj)
            {
                var reference = obj as AssemblyReference;
                return base.Equals(obj) &&
                    _referenceAssemblyWithType.AssemblyName == reference._referenceAssemblyWithType.AssemblyName;
            }

            public override int GetHashCode()
            {
                return Hash.Combine(_referenceAssemblyWithType.AssemblyName, base.GetHashCode());
            }

            private class AssemblyReferenceCodeAction : CodeAction
            {
                private readonly AssemblyReference _reference;
                private readonly string _title;
                private readonly Document _document;
                private readonly Document _newDocument;

                public override string Title => _title;

                public override ImmutableArray<string> Tags => WellKnownTagArrays.AddReference;

                private readonly Lazy<string> _lazyResolvedPath;

                public AssemblyReferenceCodeAction(
                    AssemblyReference reference,
                    Document document,
                    Document newDocument,
                    bool placeSystemNamespaceFirst)
                {
                    _reference = reference;
                    _document = document;
                    _newDocument = newDocument;

                    _title = $"{reference.provider.GetDescription(reference.SearchResult.NameParts)} ({string.Format(FeaturesResources.from_0, reference._referenceAssemblyWithType.AssemblyName)})";
                    _lazyResolvedPath = new Lazy<string>(ResolvePath);
                }

                // Adding a reference is always low priority.
                internal override CodeActionPriority Priority => CodeActionPriority.Low;

                private string ResolvePath()
                {
                    var assemblyResolverService = _document.Project.Solution.Workspace.Services.GetService<IFrameworkAssemblyPathResolver>();

                    var packageWithType = _reference._referenceAssemblyWithType;
                    var fullyQualifiedName = string.Join(".", packageWithType.ContainingNamespaceNames.Concat(packageWithType.TypeName));
                    var assemblyPath = assemblyResolverService?.ResolveAssemblyPath(
                        _document.Project.Id, packageWithType.AssemblyName, fullyQualifiedName);

                    return assemblyPath;
                }

                internal override bool PerformFinalApplicabilityCheck => true;

                internal override bool IsApplicable(Workspace workspace)
                {
                    return !string.IsNullOrWhiteSpace(_lazyResolvedPath.Value);
                }

                protected override Task<IEnumerable<CodeActionOperation>> ComputeOperationsAsync(CancellationToken cancellationToken)
                {
                    var service = _document.Project.Solution.Workspace.Services.GetService<IMetadataService>();
                    var resolvedPath = _lazyResolvedPath.Value;
                    var reference = service.GetReference(resolvedPath, MetadataReferenceProperties.Assembly);

                    // Now add the actual assembly reference.
                    var newProject = _newDocument.Project;
                    newProject = newProject.WithMetadataReferences(
                        newProject.MetadataReferences.Concat(reference));

                    var operation = new ApplyChangesOperation(newProject.Solution);
                    return Task.FromResult(SpecializedCollections.SingletonEnumerable<CodeActionOperation>(operation));
                }
            }
        }
    }
}