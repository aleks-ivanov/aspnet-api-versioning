﻿namespace Microsoft.AspNet.OData.Routing
{
    using Microsoft.AspNet.OData.Routing.Template;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.AspNetCore.Mvc.Abstractions;
    using Microsoft.AspNetCore.Mvc.Controllers;
    using Microsoft.AspNetCore.Mvc.ModelBinding;
    using Microsoft.AspNetCore.Mvc.Routing;
    using Microsoft.AspNetCore.Mvc.Versioning;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Options;
    using Microsoft.OData;
    using Microsoft.OData.Edm;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using static Microsoft.AspNet.OData.Routing.ODataRouteActionType;
    using static Microsoft.AspNetCore.Mvc.ModelBinding.BindingSource;
    using static Microsoft.AspNetCore.Mvc.Versioning.ApiVersionMapping;
    using static System.Linq.Enumerable;
    using static System.StringComparison;

    sealed class ODataRouteBindingInfoConvention : IODataActionDescriptorConvention
    {
        readonly IOptions<ODataApiVersioningOptions> options;

        internal ODataRouteBindingInfoConvention(
            IODataRouteCollectionProvider routeCollectionProvider,
            IModelMetadataProvider modelMetadataProvider,
            IOptions<ODataApiVersioningOptions> options )
        {
            RouteCollectionProvider = routeCollectionProvider;
            ModelMetadataProvider = modelMetadataProvider;
            this.options = options;
        }

        IODataRouteCollectionProvider RouteCollectionProvider { get; }

        IModelMetadataProvider ModelMetadataProvider { get; }

        ODataApiVersioningOptions Options => options.Value;

        public void Apply( ActionDescriptorProviderContext context, ControllerActionDescriptor action )
        {
            var model = action.GetApiVersionModel( Explicit | Implicit );

            UpdateControllerName( action );

            var routeInfos = model.IsApiVersionNeutral ?
                             ExpandVersionNeutralActions( action ) :
                             ExpandVersionedActions( action, model );

            foreach ( var routeInfo in routeInfos )
            {
                context.Results.Add( Clone( action, routeInfo ) );
            }
        }

        IEnumerable<ODataAttributeRouteInfo> ExpandVersionedActions( ControllerActionDescriptor action, ApiVersionModel model )
        {
            var mappings = RouteCollectionProvider.Items;
            var routeInfos = new HashSet<ODataAttributeRouteInfo>( new ODataAttributeRouteInfoComparer() );
            var declaredVersions = model.DeclaredApiVersions;
            var metadata = action.ControllerTypeInfo.IsMetadataController();

            for ( var i = 0; i < declaredVersions.Count; i++ )
            {
                for ( var j = 0; j < mappings.Count; j++ )
                {
                    var mapping = mappings[j];
                    var selector = mapping.ModelSelector;

                    if ( !selector.Contains( declaredVersions[i] ) )
                    {
                        continue;
                    }

                    if ( metadata )
                    {
                        UpdateBindingInfo( action, mapping, routeInfos );
                    }
                    else
                    {
                        var mappedVersions = selector.ApiVersions;

                        for ( var k = 0; k < mappedVersions.Count; k++ )
                        {
                            UpdateBindingInfo( action, mappedVersions[k], mapping, routeInfos );
                        }
                    }
                }
            }

            return routeInfos;
        }

        IEnumerable<ODataAttributeRouteInfo> ExpandVersionNeutralActions( ControllerActionDescriptor action )
        {
            var mappings = RouteCollectionProvider.Items;
            var routeInfos = new HashSet<ODataAttributeRouteInfo>( new ODataAttributeRouteInfoComparer() );
            var visited = new HashSet<ApiVersion>();

            for ( var i = 0; i < mappings.Count; i++ )
            {
                var mapping = mappings[i];
                var mappedVersions = mapping.ModelSelector.ApiVersions;

                for ( var j = 0; j < mappedVersions.Count; j++ )
                {
                    var apiVersion = mappedVersions[j];

                    if ( visited.Add( apiVersion ) )
                    {
                        UpdateBindingInfo( action, apiVersion, mapping, routeInfos );
                    }
                }
            }

            return routeInfos;
        }

        static void UpdateBindingInfo(
            ControllerActionDescriptor action,
            ODataRouteMapping mapping,
            ICollection<ODataAttributeRouteInfo> routeInfos )
        {
            string template;
            string path;

            switch ( action.ActionName )
            {
                case nameof( MetadataController.GetMetadata ):
                case nameof( VersionedMetadataController.GetOptions ):
                    path = "$metadata";

                    if ( string.IsNullOrEmpty( mapping.RoutePrefix ) )
                    {
                        template = path;
                    }
                    else
                    {
                        template = mapping.RoutePrefix + '/' + path;
                    }

                    break;
                default:
                    path = "/";
                    template = string.IsNullOrEmpty( mapping.RoutePrefix ) ? path : mapping.RoutePrefix;
                    break;
            }

            var handler = mapping.Services.GetRequiredService<IODataPathTemplateHandler>();
            var routeInfo = new ODataAttributeRouteInfo()
            {
                Name = mapping.RouteName,
                Template = template,
                ODataTemplate = handler.ParseTemplate( path, mapping.Services ),
                RoutePrefix = mapping.RoutePrefix,
            };

            routeInfos.Add( routeInfo );
        }

        void UpdateBindingInfo(
            ControllerActionDescriptor action,
            ApiVersion apiVersion,
            ODataRouteMapping mapping,
            ICollection<ODataAttributeRouteInfo> routeInfos )
        {
            var routeContext = new ODataRouteBuilderContext( apiVersion, mapping, action, Options );

            if ( routeContext.IsRouteExcluded )
            {
                return;
            }

            var routeBuilder = new ODataRouteBuilder( routeContext );
            var parameterContext = new ActionParameterContext( routeBuilder, routeContext );

            if ( !parameterContext.IsSupported )
            {
                return;
            }

            for ( var i = 0; i < action.Parameters.Count; i++ )
            {
                UpdateBindingInfo( parameterContext, action.Parameters[i] );
            }

            var routeInfo = new ODataAttributeRouteInfo()
            {
                Name = mapping.RouteName,
                Template = routeBuilder.BuildPath( includePrefix: true ),
                ODataTemplate = parameterContext.PathTemplate,
                RoutePrefix = mapping.RoutePrefix,
            };

            routeInfos.Add( routeInfo );
        }

        void UpdateBindingInfo( ActionParameterContext context, ParameterDescriptor parameter )
        {
            var parameterType = parameter.ParameterType;
            var bindingInfo = parameter.BindingInfo;

            if ( bindingInfo != null )
            {
                if ( ( parameterType.IsODataQueryOptions() || parameterType.IsODataPath() ) && bindingInfo.BindingSource == Custom )
                {
                    bindingInfo.BindingSource = Special;
                }

                return;
            }

            var metadata = ModelMetadataProvider.GetMetadataForType( parameterType );

            parameter.BindingInfo = bindingInfo = new BindingInfo() { BindingSource = metadata.BindingSource };

            if ( bindingInfo.BindingSource != null )
            {
                if ( ( parameterType.IsODataQueryOptions() || parameterType.IsODataPath() ) && bindingInfo.BindingSource == Custom )
                {
                    bindingInfo.BindingSource = Special;
                }

                return;
            }

            var key = default( IEdmNamedElement );
            var paramName = parameter.Name;
            var source = Query;

            switch ( context.RouteContext.ActionType )
            {
                case EntitySet:

                    var keys = context.RouteContext.EntitySet.EntityType().Key().ToArray();

                    key = keys.FirstOrDefault( k => k.Name.Equals( paramName, OrdinalIgnoreCase ) );

                    if ( key == null )
                    {
                        var template = context.PathTemplate;

                        if ( template != null )
                        {
                            var segments = template.Segments.OfType<KeySegmentTemplate>();

                            if ( segments.SelectMany( s => s.ParameterMappings.Values ).Any( name => name.Equals( paramName, OrdinalIgnoreCase ) ) )
                            {
                                source = Path;
                            }
                        }
                    }
                    else
                    {
                        source = Path;
                    }

                    break;
                case BoundOperation:
                case UnboundOperation:

                    var operation = context.RouteContext.Operation;

                    if ( operation == null )
                    {
                        break;
                    }

                    key = operation.Parameters.FirstOrDefault( p => p.Name.Equals( paramName, OrdinalIgnoreCase ) );

                    if ( key == null )
                    {
                        if ( operation.IsBound )
                        {
                            goto case EntitySet;
                        }
                    }
                    else
                    {
                        source = Path;
                    }

                    break;
            }

            bindingInfo.BindingSource = source;
            parameter.BindingInfo = bindingInfo;
        }

        static ControllerActionDescriptor Clone( ControllerActionDescriptor action, AttributeRouteInfo attributeRouteInfo )
        {
            var clone = new ControllerActionDescriptor()
            {
                ActionConstraints = action.ActionConstraints,
                ActionName = action.ActionName,
                AttributeRouteInfo = attributeRouteInfo,
                BoundProperties = action.BoundProperties,
                ControllerName = action.ControllerName,
                ControllerTypeInfo = action.ControllerTypeInfo,
                DisplayName = action.DisplayName,
                FilterDescriptors = action.FilterDescriptors,
                MethodInfo = action.MethodInfo,
                Parameters = action.Parameters,
                Properties = action.Properties,
                RouteValues = action.RouteValues,
            };

            return clone;
        }

        static void UpdateControllerName( ControllerActionDescriptor action )
        {
            if ( !action.RouteValues.TryGetValue( "controller", out var key ) )
            {
                key = action.ControllerName;
            }

            action.ControllerName = TrimTrailingNumbers( key );
        }

        static string TrimTrailingNumbers( string name )
        {
            if ( string.IsNullOrEmpty( name ) )
            {
                return name;
            }

            var last = name.Length - 1;

            for ( var i = last; i >= 0; i-- )
            {
                if ( !char.IsNumber( name[i] ) )
                {
                    if ( i < last )
                    {
                        return name.Substring( 0, i + 1 );
                    }

                    return name;
                }
            }

            return name;
        }

        sealed class ODataAttributeRouteInfoComparer : IEqualityComparer<ODataAttributeRouteInfo>
        {
            public bool Equals( ODataAttributeRouteInfo? x, ODataAttributeRouteInfo? y )
            {
                if ( x == null )
                {
                    return y == null;
                }
                else if ( y == null )
                {
                    return false;
                }

                var comparer = StringComparer.OrdinalIgnoreCase;

                return comparer.Equals( x.Template, y.Template ) &&
                       comparer.Equals( x.Name, y.Name );
            }

            public int GetHashCode( ODataAttributeRouteInfo obj ) =>
                obj is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode( obj.Template );
        }
    }
}