﻿using System.Collections.ObjectModel;
using JSONAPI.Attributes;
using JSONAPI.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Formatting;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web.Http;
using JSONAPI.Extensions;
using JSONAPI.Payload;

namespace JSONAPI.Json
{
    public class JsonApiFormatter : JsonMediaTypeFormatter
    {
        public JsonApiFormatter(IModelManager modelManager) :
            this(modelManager, new ErrorSerializer())
        {
        }

        public JsonApiFormatter(IPluralizationService pluralizationService) :
            this(new ModelManager(pluralizationService))
        {
        }

        // Currently for tests only.
        internal JsonApiFormatter(IErrorSerializer errorSerializer)
            : this(new ModelManager(new PluralizationService()), errorSerializer)
        {
            
        }

        internal JsonApiFormatter(IModelManager modelManager, IErrorSerializer errorSerializer)
        {
            _modelManager = modelManager;
            _errorSerializer = errorSerializer;
            SupportedMediaTypes.Insert(0, new MediaTypeHeaderValue("application/vnd.api+json"));
            ValidateRawJsonStrings = true;
        }

        public bool ValidateRawJsonStrings { get; set; }

        [Obsolete("Use ModelManager.PluralizationService instead")]
        public IPluralizationService PluralizationService  //FIXME: Deprecated, will be removed shortly
        {
            get
            {
                return _modelManager.PluralizationService;
            }
        }

        private readonly IErrorSerializer _errorSerializer;

        private readonly IModelManager _modelManager;
        public IModelManager ModelManager
        {
            get
            {
                return _modelManager;
            }
        }

        private Lazy<Dictionary<Stream, RelationAggregator>> _relationAggregators
            = new Lazy<Dictionary<Stream, RelationAggregator>>(
                () => new Dictionary<Stream, RelationAggregator>()
            );
        public Dictionary<Stream, RelationAggregator> RelationAggregators
        {
            get { return _relationAggregators.Value; }
        }

        public override bool CanReadType(Type t)
        {
            return true;
        }

        public override bool CanWriteType(Type type)
        {
            return true;
        }

        private const string PrimaryDataKeyName = "data";
        private const string AttributesKeyName = "attributes";
        private const string RelationshipsKeyName = "relationships";
        private const string IncludedKeyName = "included";
        private const string LinksKeyName = "links";
        private const string RelatedKeyName = "related";

        #region Serialization

        public override Task WriteToStreamAsync(System.Type type, object value, Stream writeStream, System.Net.Http.HttpContent content, System.Net.TransportContext transportContext)
        {
            RelationAggregator aggregator;
            lock (this.RelationAggregators)
            {
                if (this.RelationAggregators.ContainsKey(writeStream))
                    aggregator = this.RelationAggregators[writeStream];
                else
                {
                    aggregator = new RelationAggregator();
                    this.RelationAggregators[writeStream] = aggregator;
                }
            }

            var contentHeaders = content == null ? null : content.Headers;
            var effectiveEncoding = SelectCharacterEncoding(contentHeaders);
            JsonWriter writer = this.CreateJsonWriter(typeof(object), writeStream, effectiveEncoding);
            JsonSerializer serializer = this.CreateJsonSerializer();
            if (_errorSerializer.CanSerialize(type))
            {
                // `value` is an error
                _errorSerializer.SerializeError(value, writeStream, writer, serializer);
            }
            else
            {
                var payload = value as IPayload;
                if (payload != null)
                {
                    value = payload.PrimaryData;
                }
            
                writer.WriteStartObject();
                writer.WritePropertyName(PrimaryDataKeyName);

                if (value == null)
                {
                    if (_modelManager.IsSerializedAsMany(type))
                    {
                        writer.WriteStartArray();
                        writer.WriteEndArray();
                    }
                    else
                    {
                        writer.WriteNull();
                    }
                }
                else
                {
                    Type valtype = GetSingleType(value.GetType());
                    if (_modelManager.IsSerializedAsMany(value.GetType()))
                        aggregator.AddPrimary(valtype, (IEnumerable<object>) value);
                    else
                        aggregator.AddPrimary(valtype, value);

                    //writer.Formatting = Formatting.Indented;

                    if (_modelManager.IsSerializedAsMany(value.GetType()))
                        this.SerializeMany(value, writeStream, writer, serializer, aggregator);
                    else
                        this.Serialize(value, writeStream, writer, serializer, aggregator);

                    // Include links from aggregator
                    SerializeLinkedResources(writeStream, writer, serializer, aggregator);
                }

                if (payload != null && payload.Metadata != null)
                {
                    writer.WritePropertyName("meta");
                    serializer.Serialize(writer, payload.Metadata);
                }

                writer.WriteEndObject();
            }
            writer.Flush();

            lock (this.RelationAggregators)
            {
                this.RelationAggregators.Remove(writeStream);
            }

            //return base.WriteToStreamAsync(type, obj as object, writeStream, content, transportContext);

            //TODO: For now we won't worry about optimizing this down into smaller async parts, we'll just do it all synchronous. So...
            // Just return a completed task... from http://stackoverflow.com/a/17527551/489116
            var tcs = new TaskCompletionSource<object>();
            tcs.SetResult(null);
            return tcs.Task;
        }

        protected void SerializeMany(object value, Stream writeStream, JsonWriter writer, JsonSerializer serializer, RelationAggregator aggregator)
        {
            writer.WriteStartArray();
            foreach (object singleVal in (IEnumerable)value)
            {
                this.Serialize(singleVal, writeStream, writer, serializer, aggregator);
            }
            writer.WriteEndArray();
        }

        protected void Serialize(object value, Stream writeStream, JsonWriter writer, JsonSerializer serializer, RelationAggregator aggregator)
        {
            writer.WriteStartObject();

            var resourceType = value.GetType();
            var jsonTypeKey = _modelManager.GetResourceTypeNameForType(resourceType);
            var idProp = _modelManager.GetIdProperty(resourceType);

            // Write the type and id
            WriteTypeAndId(writer, resourceType, value);

            // Leverage the cached map to avoid another costly call to System.Type.GetProperties()
            var props = _modelManager.GetProperties(value.GetType());

            // Do non-model properties first, everything else goes in "related"
            //TODO: Unless embedded???
            var relationshipModelProperties = new List<RelationshipModelProperty>();

            // Write attributes
            WriteAttributes(props, writer, idProp, value, serializer, relationshipModelProperties);

            // Now do other stuff
            if (relationshipModelProperties.Count() > 0)
            {
                writer.WritePropertyName(RelationshipsKeyName);
                writer.WriteStartObject();
            }
            foreach (var relationshipModelProperty in relationshipModelProperties)
            {
                bool skip = false, 
                    iip = false;
                string lt = null;
                SerializeAsOptions sa = SerializeAsOptions.NoLinks;

                var prop = relationshipModelProperty.Property;

                object[] attrs = prop.GetCustomAttributes(true);

                foreach (object attr in attrs)
                {
                    Type attrType = attr.GetType();
                    if (typeof(JsonIgnoreAttribute).IsAssignableFrom(attrType))
                    {
                        skip = true;
                        continue;
                    }
                    if (typeof(IncludeInPayload).IsAssignableFrom(attrType))
                        iip = ((IncludeInPayload)attr).Include;
                    if (typeof(SerializeAs).IsAssignableFrom(attrType))
                        sa = ((SerializeAs)attr).How;
                    if (typeof(LinkTemplate).IsAssignableFrom(attrType))
                        lt = ((LinkTemplate)attr).TemplateString;
                }
                if (skip) continue;

                // Write the relationship's type
                writer.WritePropertyName(relationshipModelProperty.JsonKey);
                

                // Now look for enumerable-ness:
                if (typeof(IEnumerable<Object>).IsAssignableFrom(prop.PropertyType))
                {
                    writer.WriteStartObject();
                    // Write the data element
                    writer.WritePropertyName(PrimaryDataKeyName);
                    // Look out! If we want to SerializeAs a link, computing the property is probably 
                    // expensive...so don't force it just to check for null early!
                    if (sa == SerializeAsOptions.NoLinks && prop.GetValue(value, null) == null)
                    {
                        writer.WriteStartArray();
                        writer.WriteEndArray();
                        writer.WriteEndObject();
                        continue;
                    }

                    // Always write the data attribute of a relationship
                    IEnumerable<object> items = (IEnumerable<object>)prop.GetValue(value, null);
                    if (items == null)
                    {
                        // Return an empty array when there are no items
                        writer.WriteStartArray();
                        writer.WriteEndArray();
                    }
                    else
                    {
                        // Write the array with data
                        this.WriteIdsArrayJson(writer, items, serializer);
                        if (iip)
                        {
                            Type itemType;
                            if (prop.PropertyType.IsGenericType)
                            {
                                itemType = prop.PropertyType.GetGenericArguments()[0];
                            }
                            else
                            {
                                // Must be an array at this point, right??
                                itemType = prop.PropertyType.GetElementType();
                            }
                            if (aggregator != null) aggregator.Add(itemType, items); // should call the IEnumerable one...right?
                        }
                    } 

                    // in case there is also a link defined, add it to the relationship
                    if (sa == SerializeAsOptions.RelatedLink) 
                    {
                        if (lt == null) throw new JsonSerializationException("A property was decorated with SerializeAs(SerializeAsOptions.Link) but no LinkTemplate attribute was provided.");
                        //TODO: Check for "{0}" in linkTemplate and (only) if it's there, get the Ids of all objects and "implode" them.
                        string href = String.Format(lt, null, GetIdFor(value));
                        //writer.WritePropertyName(ContractResolver._modelManager.GetJsonKeyForProperty(prop));
                        //TODO: Support ids and type properties in "link" object

                        writer.WritePropertyName(LinksKeyName);
                        writer.WriteStartObject();
                        writer.WritePropertyName(RelatedKeyName);
                        writer.WriteValue(href);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndObject();
                }
                else
                {
                    var propertyValue = prop.GetValue(value, null);

                    writer.WriteStartObject();
                    // Write the data element
                    writer.WritePropertyName(PrimaryDataKeyName);
                    // Look out! If we want to SerializeAs a link, computing the property is probably 
                    // expensive...so don't force it just to check for null early!
                    if (sa == SerializeAsOptions.NoLinks && propertyValue == null)
                    {
                        writer.WriteNull();
                        writer.WriteEndObject();
                        continue;
                    }

                    Lazy<string> objId = new Lazy<String>(() => GetIdFor(propertyValue));

                    // Write the resource identifier object
                    WriteResourceIdentifierObject(writer, prop.PropertyType, propertyValue);

                    if (iip)
                        if (aggregator != null)
                            aggregator.Add(prop.PropertyType, propertyValue);

                    // If there are links write them next to the data object
                    if (sa == SerializeAsOptions.RelatedLink)
                    {
                        if (lt == null)
                            throw new JsonSerializationException(
                                "A property was decorated with SerializeAs(SerializeAsOptions.Link) but no LinkTemplate attribute was provided.");
                        var relatedObjectId = lt.Contains("{0}") ? objId.Value : null;
                        string link = String.Format(lt, relatedObjectId, GetIdFor(value));

                        writer.WritePropertyName(LinksKeyName);
                        writer.WriteStartObject();
                        writer.WritePropertyName(RelatedKeyName);
                        writer.WriteValue(link);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndObject();
                }
            }
            if (relationshipModelProperties.Count() > 0)
            {
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        protected void SerializeLinkedResources(Stream writeStream, JsonWriter writer, JsonSerializer serializer, RelationAggregator aggregator)
        {
            /* This is a bit messy, because we may add items of a given type to the
             * set we are currently processing. Not only is this an issue because you
             * can't modify a set while you're enumerating it (hence why we make a
             * copy first), but we need to catch the newly added objects and process
             * them as well. So, we have to keep making passes until we detect that
             * we haven't added any new objects to any of the appendices.
             */
            Dictionary<Type, ISet<object>>
                processed = new Dictionary<Type, ISet<object>>(),
                toBeProcessed = new Dictionary<Type, ISet<object>>(); // is this actually necessary?
            /* On top of that, we need a new JsonWriter for each appendix--because we
             * may write objects of type A, then while processing type B find that
             * we need to write more objects of type A! So we can't keep appending
             * to the same writer.
             */
            /* Oh, and we have to keep a reference to the TextWriter of the JsonWriter
             * because there's no member to get it back out again. ?!?
             * */

            int numAdditions;

            if (aggregator.Appendices.Count > 0)
            {
                writer.WritePropertyName(IncludedKeyName);
                writer.WriteStartArray();

                do
                {
                    numAdditions = 0;
                    Dictionary<Type, ISet<object>> appxs = new Dictionary<Type, ISet<object>>(aggregator.Appendices); // shallow clone, in case we add a new type during enumeration!
                    foreach (KeyValuePair<Type, ISet<object>> apair in appxs)
                    {
                        Type type = apair.Key;
                        ISet<object> appendix = apair.Value;

                        HashSet<object> tbp;
                        if (processed.ContainsKey(type))
                        {
                            toBeProcessed[type] = tbp = new HashSet<object>(appendix.Except(processed[type]));
                        }
                        else
                        {
                            toBeProcessed[type] = tbp = new HashSet<object>(appendix);
                            processed[type] = new HashSet<object>();
                        }

                        if (tbp.Count > 0)
                        {
                            numAdditions += tbp.Count;
                            foreach (object obj in tbp)
                            {
                                Serialize(obj, writeStream, writer, serializer, aggregator); // Note, not writer, but jw--we write each type to its own JsonWriter and combine them later.
                            }
                            processed[type].UnionWith(tbp);
                        }
                    }
                } while (numAdditions > 0);

                writer.WriteEndArray();
            }


        }

        private void WriteAttributes(ModelProperty[] props, JsonWriter writer, PropertyInfo idProp, object value, JsonSerializer serializer, List<RelationshipModelProperty> relationshipModelProperties)
        {
            if (props.Count() > 0)
            {
                writer.WritePropertyName(AttributesKeyName);
                writer.WriteStartObject();
            }

            foreach (var modelProperty in props)
            {
                var prop = modelProperty.Property;
                if (prop == idProp) continue; // Don't write the "id" property twice!

                if (modelProperty is FieldModelProperty)
                {
                    if (modelProperty.IgnoreByDefault) continue; // TODO: allow overriding this

                    // numbers, strings, dates...
                    writer.WritePropertyName(modelProperty.JsonKey);

                    var propertyValue = prop.GetValue(value, null);

                    if (prop.PropertyType == typeof(Decimal) || prop.PropertyType == typeof(Decimal?))
                    {
                        if (propertyValue == null)
                            writer.WriteNull();
                        else
                            writer.WriteValue(propertyValue);
                    }
                    else if (prop.PropertyType == typeof(string) &&
                        prop.GetCustomAttributes().Any(attr => attr is SerializeStringAsRawJsonAttribute))
                    {
                        if (propertyValue == null)
                        {
                            writer.WriteNull();
                        }
                        else
                        {
                            var json = (string)propertyValue;
                            if (ValidateRawJsonStrings)
                            {
                                try
                                {
                                    var token = JToken.Parse(json);
                                    json = token.ToString();
                                }
                                catch (Exception)
                                {
                                    json = "{}";
                                }
                            }
                            var valueToSerialize = JsonHelpers.MinifyJson(json);
                            writer.WriteRawValue(valueToSerialize);
                        }
                    }
                    else
                    {
                        serializer.Serialize(writer, propertyValue);
                    }
                }
                else if (modelProperty is RelationshipModelProperty)
                {
                    relationshipModelProperties.Add((RelationshipModelProperty)modelProperty);
                }
            }

            if (props.Count() > 0)
                writer.WriteEndObject();
        }

        #endregion Serialization

        #region Deserialization

        private class BadRequestException : Exception
        {
            public BadRequestException(string message)
                : base(message)
            {
                
            }
        }

        public override Task<object> ReadFromStreamAsync(Type type, Stream readStream, HttpContent content, IFormatterLogger formatterLogger)
        {
            return Task.FromResult(ReadFromStream(type, readStream, content, formatterLogger)); ;
        }

        private object ReadFromStream(Type type, Stream readStream, HttpContent content, IFormatterLogger formatterLogger)
        {
            object retval = null;
            Type singleType = GetSingleType(type);
            var contentHeaders = content == null ? null : content.Headers;

            // If content length is 0 then return default value for this type
            if (contentHeaders != null && contentHeaders.ContentLength == 0)
                return GetDefaultValueForType(type);


            try
            {

                var effectiveEncoding = SelectCharacterEncoding(contentHeaders);
                JsonReader reader = this.CreateJsonReader(typeof (IDictionary<string, object>), readStream,
                    effectiveEncoding);

                reader.Read();
                if (reader.TokenType != JsonToken.StartObject)
                    throw new JsonSerializationException("Document root is not an object!");

                bool foundPrimaryData = false;
                while (reader.Read())
                {
                    if (reader.TokenType == JsonToken.PropertyName)
                    {
                        string value = (string) reader.Value;
                        reader.Read(); // burn the PropertyName token
                        switch (value)
                        {
                            case IncludedKeyName:
                                //TODO: If we want to capture linked/related objects in a compound document when deserializing, do it here...do we?
                                reader.Skip();
                                break;
                            case RelationshipsKeyName:
                                // ignore this, is it even meaningful in a PUT/POST body?
                                reader.Skip();
                                break;
                            case PrimaryDataKeyName:
                                // Could be a single resource or multiple, according to spec!
                                foundPrimaryData = true;
                                retval = DeserializePrimaryData(singleType, reader);
                                break;
                            case AttributesKeyName:
                                // Could be a single resource or multiple, according to spec!
                                foundPrimaryData = true;
                                retval = DeserializePrimaryData(singleType, reader);
                                break;
                        }
                    }
                    else
                        reader.Skip();
                }

                if (!foundPrimaryData)
                    throw new BadRequestException(String.Format("Expected primary data located at the `{0}` key", PrimaryDataKeyName));


                /* WARNING: May transform a single object into a list of objects!!!
                 * This is a necessary workaround to support POST and PUT of multiple 
                 * resoruces as per the spec, because WebAPI does NOT allow you to overload 
                 * a POST or PUT method based on the input parameter...i.e., you cannot 
                 * have a method that responds to POST of a single resource and a second 
                 * method that responds to the POST of a collection (list) of resources!
                 */
                if (retval != null)
                {
                    if (!type.IsAssignableFrom(retval.GetType()) && _modelManager.IsSerializedAsMany(type))
                    {
                        IList list = (IList) Activator.CreateInstance(typeof (List<>).MakeGenericType(singleType));
                        list.Add(retval);
                        return list;
                    }
                    else
                    {
                        return retval;
                    }
                }

            }
            catch (BadRequestException ex)
            {
                // We have to perform our own serialization of the error response here.
                var response = new HttpResponseMessage(HttpStatusCode.BadRequest);

                using (var writeStream = new MemoryStream())
                {
                    var effectiveEncoding = SelectCharacterEncoding(contentHeaders);
                    JsonWriter writer = CreateJsonWriter(typeof (object), writeStream, effectiveEncoding);
                    JsonSerializer serializer = CreateJsonSerializer();

                    var httpError = new HttpError(ex, true);
                        // TODO: allow consumer to choose whether to include error detail
                    _errorSerializer.SerializeError(httpError, writeStream, writer, serializer);

                    writer.Flush();
                    writeStream.Flush();
                    writeStream.Seek(0, SeekOrigin.Begin);

                    using (var stringReader = new StreamReader(writeStream))
                    {
                        var stringContent = stringReader.ReadToEnd(); // TODO: use async version
                        response.Content = new StringContent(stringContent, Encoding.UTF8, "application/vnd.api+json");
                    }
                }

                throw new HttpResponseException(response);
            }
            catch (Exception e)
            {
                if (formatterLogger == null)
                {
                    throw;
                }
                formatterLogger.LogError(String.Empty, e);
                return GetDefaultValueForType(type);
            }

            return GetDefaultValueForType(type);
        }

        private object DeserializePrimaryData(Type singleType, JsonReader reader)
        {
            object retval;
            if (reader.TokenType == JsonToken.StartArray)
            {
                Type listType = (typeof(List<>)).MakeGenericType(singleType);
                retval = (IList)Activator.CreateInstance(listType);
                reader.Read(); // Burn off StartArray token
                while (reader.TokenType == JsonToken.StartObject)
                {
                    ((IList)retval).Add(Deserialize(singleType, reader));
                }
                // burn EndArray token...
                if (reader.TokenType != JsonToken.EndArray)
                    throw new JsonReaderException(
                        String.Format("Expected JsonToken.EndArray but got {0}",
                            reader.TokenType));
                reader.Read();
            }
            else if (reader.TokenType == JsonToken.StartObject)
            {
                // Because we choose what to deserialize based on the ApiController method signature
                // (not choose the method signature based on what we're deserializing), the `type`
                // parameter will always be `IList<Model>` even if a single model is sent!
                retval = Deserialize(singleType, reader);
            }
            else
            {
                throw new BadRequestException(String.Format("Unexpected value for the `{0}` key", PrimaryDataKeyName));
            }

            return retval;
        }

        private object Deserialize(Type objectType, JsonReader reader, object obj = null)
        {
            object retval = obj == null ? Activator.CreateInstance(objectType) : obj;

            if (reader.TokenType != JsonToken.StartObject) throw new JsonReaderException(String.Format("Expected JsonToken.StartObject, got {0}", reader.TokenType.ToString()));
            reader.Read(); // Burn the StartObject token
            do
            {
                if (reader.TokenType == JsonToken.PropertyName)
                {
                    string value = (string)reader.Value;
                    var modelProperty = _modelManager.GetPropertyForJsonKey(objectType, value);

                    if (value == RelationshipsKeyName)
                    {
                        // This can only happen within a links object
                        reader.Read(); // burn the PropertyName token
                        //TODO: linked resources (Done??)
                        DeserializeLinkedResources(retval, reader);
                    }
                    else if (value == AttributesKeyName) {
                        reader.Read();

                        retval = Deserialize(objectType, reader, retval);
                    }
                    else if (modelProperty != null)
                    {
                        if (!(modelProperty is FieldModelProperty))
                        {
                            reader.Read(); // burn the PropertyName token
                            //TODO: Embedded would be dropped here!
                            continue; // These aren't supposed to be here, they're supposed to be in "related"!
                        }

                        var prop = modelProperty.Property;

                        object propVal;
                        Type enumType;
                        if (prop.PropertyType == typeof(string) &&
                            prop.GetCustomAttributes().Any(attr => attr is SerializeStringAsRawJsonAttribute))
                        {
                            reader.Read();
                            if (reader.TokenType == JsonToken.Null)
                            {
                                propVal = null;
                            }
                            else
                            {
                                var token = JToken.Load(reader);
                                var rawPropVal = token.ToString();
                                propVal = JsonHelpers.MinifyJson(rawPropVal);
                            }
                        }
                        else if (prop.PropertyType.IsGenericType &&
                                 prop.PropertyType.GetGenericTypeDefinition() == typeof(Nullable<>) &&
                                 (enumType = prop.PropertyType.GetGenericArguments()[0]).IsEnum)
                        {
                            // Nullable enums need special handling
                            reader.Read();
                            propVal = reader.TokenType == JsonToken.Null
                                ? null
                                : Enum.Parse(enumType, reader.Value.ToString());
                        }
                        else if (prop.PropertyType == typeof(DateTimeOffset) ||
                                 prop.PropertyType == typeof(DateTimeOffset?))
                        {
                            // For some reason 
                            reader.ReadAsString();
                            propVal = reader.TokenType == JsonToken.Null
                                ? (object) null
                                : DateTimeOffset.Parse(reader.Value.ToString());
                        }
                        else
                        {
                            reader.Read();
                            propVal = DeserializeAttribute(prop.PropertyType, reader);
                        }


                        prop.SetValue(retval, propVal, null);

                        // Tell the MetadataManager that we deserialized this property
                        MetadataManager.Instance.SetMetaForProperty(retval, prop, true);

                        // pop the value off the reader, so we catch the EndObject token below!.
                        reader.Read();
                    }
                    else
                    {
                        // Unexpected/unknown property--Skip the propertyname and its value
                        reader.Skip();
                        if (reader.TokenType == JsonToken.StartArray || reader.TokenType == JsonToken.StartObject) reader.Skip();
                        else reader.Read();
                    }
                }

            } while (reader.TokenType != JsonToken.EndObject);
            reader.Read(); // burn the EndObject token before returning back up the call stack

            return retval;
        }

        private void DeserializeLinkedResources(object obj, JsonReader reader)
        {
            if (reader.TokenType != JsonToken.StartObject) throw new JsonSerializationException("'links' property is not an object!");

            Type objectType = obj.GetType();

            while (reader.Read())
            {
                if (reader.TokenType == JsonToken.EndObject)
                {
                    reader.Read(); // Burn the EndObject token
                    break;
                }

                if (reader.TokenType != JsonToken.PropertyName)
                    throw new BadRequestException(String.Format("Unexpected token: {0}", reader.TokenType));

                var value = (string)reader.Value;
                reader.Read(); // burn the PropertyName token
                var modelProperty = _modelManager.GetPropertyForJsonKey(objectType, value) as RelationshipModelProperty;
                if (modelProperty == null)
                {
                    reader.Skip();
                    continue;
                }

                var relationshipToken = JToken.ReadFrom(reader);
                if (!(relationshipToken is JObject))
                    throw new BadRequestException("Each relationship key on a links object must have an object value.");

                var relationshipObject = (JObject) relationshipToken;
                var linkageToken = relationshipObject[PrimaryDataKeyName];

                var linkageObjects = new List<Tuple<string, string>>();

                if (modelProperty.IsToMany)
                {
                    if (linkageToken == null)
                        throw new BadRequestException("Expected an array value for `linkage` but no `linkage` key was found.");

                    if (linkageToken.Type != JTokenType.Array)
                        throw new BadRequestException("Expected an array value for `linkage` but got " + linkageToken.Type + ".");

                    foreach (var element in (JArray) linkageToken)
                    {
                        if (!(element is JObject))
                            throw new BadRequestException("Each element in the `linkage` array must be an object.");

                        var linkageObject = DeserializeLinkageObject((JObject) element);
                        linkageObjects.Add(linkageObject);
                    }
                }
                else
                {
                    if (linkageToken == null)
                        throw new BadRequestException("Expected an object or null value for `linkage` but no `linkage` key was found.");

                    switch (linkageToken.Type)
                    {
                        case JTokenType.Null:
                            break;
                        case JTokenType.Object:
                            linkageObjects.Add(DeserializeLinkageObject((JObject)linkageToken));
                            break;
                        default:
                            throw new BadRequestException("Expected an object value for `linkage` but got " + linkageToken.Type + ".");
                    }
                }

                var relatedStubs = linkageObjects.Select(lo =>
                {
                    var resourceType = _modelManager.GetTypeByResourceTypeName(lo.Item2);
                    return GetById(resourceType, lo.Item1);
                }).ToArray();

                var prop = modelProperty.Property;
                if (!modelProperty.IsToMany)
                {
                    // To-one relationship

                    var relatedStub = relatedStubs.FirstOrDefault();
                    prop.SetValue(obj, relatedStub);
                }
                else
                {
                    // To-many relationship

                    var hmrel = (IEnumerable<Object>) prop.GetValue(obj, null);
                    if (hmrel == null)
                    {
                        hmrel = prop.PropertyType.CreateEnumerableInstance();
                        if (hmrel == null)
                            // punt!
                            throw new JsonReaderException(
                                String.Format(
                                    "Could not create empty container for relationship property {0}!",
                                    prop));
                    }

                    // We're having problems with how to generalize/cast/generic-ize this code, so for the time
                    // being we'll brute-force it in super-dynamic language style...
                    Type hmtype = hmrel.GetType();
                    MethodInfo add = hmtype.GetMethod("Add");

                    foreach (var stub in relatedStubs)
                    {
                        add.Invoke(hmrel, new[] {stub});
                    }

                    prop.SetValue(obj, hmrel);
                }

                // Tell the MetadataManager that we deserialized this property
                MetadataManager.Instance.SetMetaForProperty(obj, prop, true);
            }
        }

        private object DeserializeAttribute(Type type, JsonReader reader)
        {
            if (reader.TokenType == JsonToken.Null)
                return null;

            var token = JToken.ReadFrom(reader);
            return token.ToObject(type);
        }

        private static Tuple<string, string> DeserializeLinkageObject(JObject token)
        {
            var relatedObjectType = token["type"] as JValue;
            if (relatedObjectType == null || relatedObjectType.Type != JTokenType.String)
                throw new BadRequestException("Each linkage object must have a string value for the key `type`.");

            var relatedObjectTypeValue = relatedObjectType.Value<string>();
            if (string.IsNullOrWhiteSpace(relatedObjectTypeValue))
                throw new BadRequestException("The value for `type` must be specified.");

            var relatedObjectId = token["id"] as JValue;
            if (relatedObjectId == null || relatedObjectId.Type != JTokenType.String)
                throw new BadRequestException("Each linkage object must have a string value for the key `id`.");

            var relatedObjectIdValue = relatedObjectId.Value<string>();
            if (string.IsNullOrWhiteSpace(relatedObjectIdValue))
                throw new BadRequestException("The value for `id` must be specified.");

            return Tuple.Create(relatedObjectIdValue, relatedObjectType.Value<string>());
        }

        #endregion

        private Type GetSingleType(Type type)//dynamic value = null)
        {
            return _modelManager.IsSerializedAsMany(type) ? _modelManager.GetElementType(type) : type;
        }

        protected object GetById(Type type, string id)
        {
            // Only good for creating dummy relationship objects...
            object retval = Activator.CreateInstance(type);
            PropertyInfo idprop = _modelManager.GetIdProperty(type);
            idprop.SetValue(retval, System.Convert.ChangeType(id, idprop.PropertyType));
            return retval;
        }

        protected string GetValueForIdProperty(PropertyInfo idprop, object obj)
        {
            if (idprop != null)
            {
                return idprop.GetValue(obj).ToString();
            }
            return "NOIDCOMPUTABLE!";
        }

        protected string GetIdFor(object obj)
        {
            Type type = obj.GetType();
            PropertyInfo idprop = _modelManager.GetIdProperty(type);
            return GetValueForIdProperty(idprop, obj);
        }

        private void WriteResourceIdentifierObject(JsonWriter writer, Type propertyType, object propertyValue)
        {
            writer.WriteStartObject();
            WriteTypeAndId(writer, propertyType, propertyValue);
            writer.WriteEndObject();
        }

        private void WriteTypeAndId(JsonWriter writer, Type propertyType, object propertyValue)
        {
            writer.WritePropertyName("type");
            writer.WriteValue(_modelManager.GetResourceTypeNameForType(propertyType));

            writer.WritePropertyName("id");
            writer.WriteValue(GetValueForIdProperty(_modelManager.GetIdProperty(propertyType), propertyValue));
        }

        private void WriteIdsArrayJson(Newtonsoft.Json.JsonWriter writer, IEnumerable<object> value, Newtonsoft.Json.JsonSerializer serializer)
        {
            IEnumerator<Object> collectionEnumerator = (value as IEnumerable<object>).GetEnumerator();
            writer.WriteStartArray();
            while (collectionEnumerator.MoveNext())
            {
                WriteResourceIdentifierObject(writer, collectionEnumerator.Current.GetType(), collectionEnumerator.Current);
            }
            writer.WriteEndArray();
        }

    }
}
