﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. 

using System.Text.Json;
using Json.Schema;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers.ParseNodes;
using JsonSchema = Json.Schema.JsonSchema;

namespace Microsoft.OpenApi.Readers.V31
{
    /// <summary>
    /// Class containing logic to deserialize Open API V31 document into
    /// runtime Open API object model.
    /// </summary>
    internal static partial class OpenApiV31Deserializer
    {
        public static JsonSchema LoadSchema(ParseNode node)
        {
            var mapNode = node.CheckMapNode(OpenApiConstants.Schema);

            var builder = new JsonSchemaBuilder();
            var pointer = mapNode.GetReferencePointer();
            if (pointer != null)
            {
                return builder.Ref(pointer);
            }

            return node.JsonNode.Deserialize<JsonSchema>();
        }
    }

}
