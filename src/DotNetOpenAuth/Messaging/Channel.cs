﻿//-----------------------------------------------------------------------
// <copyright file="Channel.cs" company="Andrew Arnott">
//     Copyright (c) Andrew Arnott. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace DotNetOpenAuth.Messaging {
	using System;
	using System.Collections.Generic;
	using System.Collections.ObjectModel;
	using System.Diagnostics;
	using System.Diagnostics.CodeAnalysis;
	using System.Globalization;
	using System.IO;
	using System.Linq;
	using System.Net;
	using System.Text;
	using System.Web;
	using DotNetOpenAuth.Messaging.Reflection;

	/// <summary>
	/// Manages sending direct messages to a remote party and receiving responses.
	/// </summary>
	public abstract class Channel {
		/// <summary>
		/// The maximum allowable size for a 301 Redirect response before we send
		/// a 200 OK response with a scripted form POST with the parameters instead
		/// in order to ensure successfully sending a large payload to another server
		/// that might have a maximum allowable size restriction on its GET request.
		/// </summary>
		private static int indirectMessageGetToPostThreshold = 2 * 1024; // 2KB, recommended by OpenID group

		/// <summary>
		/// The template for indirect messages that require form POST to forward through the user agent.
		/// </summary>
		/// <remarks>
		/// We are intentionally using " instead of the html single quote ' below because
		/// the HtmlEncode'd values that we inject will only escape the double quote, so
		/// only the double-quote used around these values is safe.
		/// </remarks>
		private static string indirectMessageFormPostFormat = @"
<html>
<body onload=""var btn = document.getElementById('submit_button'); btn.disabled = true; btn.value = 'Login in progress'; document.getElementById('openid_message').submit()"">
<form id=""openid_message"" action=""{0}"" method=""post"" accept-charset=""UTF-8"" enctype=""application/x-www-form-urlencoded"" onSubmit=""var btn = document.getElementById('submit_button'); btn.disabled = true; btn.value = 'Login in progress'; return true;"">
{1}
	<input id=""submit_button"" type=""submit"" value=""Continue"" />
</form>
</body>
</html>
";

		/// <summary>
		/// A tool that can figure out what kind of message is being received
		/// so it can be deserialized.
		/// </summary>
		private IMessageFactory messageTypeProvider;

		/// <summary>
		/// A list of binding elements in the order they must be applied to outgoing messages.
		/// </summary>
		/// <remarks>
		/// Incoming messages should have the binding elements applied in reverse order.
		/// </remarks>
		[DebuggerBrowsable(DebuggerBrowsableState.Never)]
		private List<IChannelBindingElement> bindingElements = new List<IChannelBindingElement>();

		/// <summary>
		/// Initializes a new instance of the <see cref="Channel"/> class.
		/// </summary>
		/// <param name="messageTypeProvider">
		/// A class prepared to analyze incoming messages and indicate what concrete
		/// message types can deserialize from it.
		/// </param>
		/// <param name="bindingElements">The binding elements to use in sending and receiving messages.</param>
		protected Channel(IMessageFactory messageTypeProvider, params IChannelBindingElement[] bindingElements) {
			if (messageTypeProvider == null) {
				throw new ArgumentNullException("messageTypeProvider");
			}

			this.messageTypeProvider = messageTypeProvider;
			this.WebRequestHandler = new StandardWebRequestHandler();
			this.bindingElements = new List<IChannelBindingElement>(ValidateAndPrepareBindingElements(bindingElements));

			foreach (var element in this.bindingElements) {
				element.Channel = this;
			}
		}

		/// <summary>
		/// An event fired whenever a message is about to be encoded and sent.
		/// </summary>
		internal event EventHandler<ChannelEventArgs> Sending;

		/// <summary>
		/// Gets or sets an instance to a <see cref="IDirectWebRequestHandler"/> that will be used when 
		/// submitting HTTP requests and waiting for responses.
		/// </summary>
		/// <remarks>
		/// This defaults to a straightforward implementation, but can be set
		/// to a mock object for testing purposes.
		/// </remarks>
		public IDirectWebRequestHandler WebRequestHandler { get; set; }

		/// <summary>
		/// Gets the binding elements used by this channel, in the order they are applied to outgoing messages.
		/// </summary>
		/// <remarks>
		/// Incoming messages are processed by this binding elements in the reverse order.
		/// </remarks>
		protected internal ReadOnlyCollection<IChannelBindingElement> BindingElements {
			get {
				return this.bindingElements.AsReadOnly();
			}
		}

		/// <summary>
		/// Gets a tool that can figure out what kind of message is being received
		/// so it can be deserialized.
		/// </summary>
		protected IMessageFactory MessageFactory {
			get { return this.messageTypeProvider; }
		}

		/// <summary>
		/// Queues an indirect message (either a request or response) 
		/// or direct message response for transmission to a remote party.
		/// </summary>
		/// <param name="message">The one-way message to send</param>
		/// <returns>The pending user agent redirect based message to be sent as an HttpResponse.</returns>
		public UserAgentResponse Send(IProtocolMessage message) {
			if (message == null) {
				throw new ArgumentNullException("message");
			}
			this.PrepareMessageForSending(message);
			Logger.DebugFormat("Sending message: {0}", message);

			switch (message.Transport) {
				case MessageTransport.Direct:
					// This is a response to a direct message.
					return this.SendDirectMessageResponse(message);
				case MessageTransport.Indirect:
					var directedMessage = message as IDirectedProtocolMessage;
					if (directedMessage == null) {
						throw new ArgumentException(
							string.Format(
								CultureInfo.CurrentCulture,
								MessagingStrings.IndirectMessagesMustImplementIDirectedProtocolMessage,
								typeof(IDirectedProtocolMessage).FullName),
							"message");
					}
					if (directedMessage.Recipient == null) {
						throw new ArgumentException(MessagingStrings.DirectedMessageMissingRecipient, "message");
					}
					return this.SendIndirectMessage(directedMessage);
				default:
					throw new ArgumentException(
						string.Format(
							CultureInfo.CurrentCulture,
							MessagingStrings.UnrecognizedEnumValue,
							"Transport",
							message.Transport),
						"message");
			}
		}

		/// <summary>
		/// Gets the protocol message embedded in the given HTTP request, if present.
		/// </summary>
		/// <returns>The deserialized message, if one is found.  Null otherwise.</returns>
		/// <remarks>
		/// Requires an HttpContext.Current context.
		/// </remarks>
		/// <exception cref="InvalidOperationException">Thrown when <see cref="HttpContext.Current"/> is null.</exception>
		public IDirectedProtocolMessage ReadFromRequest() {
			return this.ReadFromRequest(this.GetRequestFromContext());
		}

		/// <summary>
		/// Gets the protocol message embedded in the given HTTP request, if present.
		/// </summary>
		/// <typeparam name="TRequest">The expected type of the message to be received.</typeparam>
		/// <param name="request">The deserialized message, if one is found.  Null otherwise.</param>
		/// <returns>True if the expected message was recognized and deserialized.  False otherwise.</returns>
		/// <remarks>
		/// Requires an HttpContext.Current context.
		/// </remarks>
		/// <exception cref="InvalidOperationException">Thrown when <see cref="HttpContext.Current"/> is null.</exception>
		/// <exception cref="ProtocolException">Thrown when a request message of an unexpected type is received.</exception>
		public bool TryReadFromRequest<TRequest>(out TRequest request)
			where TRequest : class, IProtocolMessage {
			return TryReadFromRequest<TRequest>(this.GetRequestFromContext(), out request);
		}

		/// <summary>
		/// Gets the protocol message embedded in the given HTTP request, if present.
		/// </summary>
		/// <typeparam name="TRequest">The expected type of the message to be received.</typeparam>
		/// <param name="httpRequest">The request to search for an embedded message.</param>
		/// <param name="request">The deserialized message, if one is found.  Null otherwise.</param>
		/// <returns>True if the expected message was recognized and deserialized.  False otherwise.</returns>
		/// <exception cref="InvalidOperationException">Thrown when <see cref="HttpContext.Current"/> is null.</exception>
		/// <exception cref="ProtocolException">Thrown when a request message of an unexpected type is received.</exception>
		public bool TryReadFromRequest<TRequest>(HttpRequestInfo httpRequest, out TRequest request)
			where TRequest : class, IProtocolMessage {
			IProtocolMessage untypedRequest = this.ReadFromRequest(httpRequest);
			if (untypedRequest == null) {
				request = null;
				return false;
			}

			request = untypedRequest as TRequest;
			if (request == null) {
				throw new ProtocolException(
					string.Format(
						CultureInfo.CurrentCulture,
						MessagingStrings.UnexpectedMessageReceived,
						typeof(TRequest),
						untypedRequest.GetType()));
			}

			return true;
		}

		/// <summary>
		/// Gets the protocol message embedded in the given HTTP request, if present.
		/// </summary>
		/// <typeparam name="TRequest">The expected type of the message to be received.</typeparam>
		/// <returns>The deserialized message.</returns>
		/// <remarks>
		/// Requires an HttpContext.Current context.
		/// </remarks>
		/// <exception cref="InvalidOperationException">Thrown when <see cref="HttpContext.Current"/> is null.</exception>
		/// <exception cref="ProtocolException">Thrown if the expected message was not recognized in the response.</exception>
		[SuppressMessage("Microsoft.Design", "CA1004:GenericMethodsShouldProvideTypeParameter", Justification = "This returns and verifies the appropriate message type.")]
		public TRequest ReadFromRequest<TRequest>()
			where TRequest : class, IProtocolMessage {
			return this.ReadFromRequest<TRequest>(this.GetRequestFromContext());
		}

		/// <summary>
		/// Gets the protocol message that may be embedded in the given HTTP request.
		/// </summary>
		/// <typeparam name="TRequest">The expected type of the message to be received.</typeparam>
		/// <param name="httpRequest">The request to search for an embedded message.</param>
		/// <returns>The deserialized message, if one is found.  Null otherwise.</returns>
		/// <exception cref="ProtocolException">Thrown if the expected message was not recognized in the response.</exception>
		[SuppressMessage("Microsoft.Design", "CA1004:GenericMethodsShouldProvideTypeParameter", Justification = "This returns and verifies the appropriate message type.")]
		public TRequest ReadFromRequest<TRequest>(HttpRequestInfo httpRequest)
			where TRequest : class, IProtocolMessage {
			TRequest request;
			if (this.TryReadFromRequest<TRequest>(httpRequest, out request)) {
				return request;
			} else {
				throw new ProtocolException(
					string.Format(
						CultureInfo.CurrentCulture,
						MessagingStrings.ExpectedMessageNotReceived,
						typeof(TRequest)));
			}
		}

		/// <summary>
		/// Gets the protocol message that may be embedded in the given HTTP request.
		/// </summary>
		/// <param name="httpRequest">The request to search for an embedded message.</param>
		/// <returns>The deserialized message, if one is found.  Null otherwise.</returns>
		public IDirectedProtocolMessage ReadFromRequest(HttpRequestInfo httpRequest) {
			IDirectedProtocolMessage requestMessage = this.ReadFromRequestInternal(httpRequest);
			if (requestMessage != null) {
				Logger.DebugFormat("Incoming request received: {0}", requestMessage);
				this.VerifyMessageAfterReceiving(requestMessage);
			}

			return requestMessage;
		}

		/// <summary>
		/// Sends a direct message to a remote party and waits for the response.
		/// </summary>
		/// <typeparam name="TResponse">The expected type of the message to be received.</typeparam>
		/// <param name="requestMessage">The message to send.</param>
		/// <returns>The remote party's response.</returns>
		/// <exception cref="ProtocolException">
		/// Thrown if no message is recognized in the response
		/// or an unexpected type of message is received.
		/// </exception>
		[SuppressMessage("Microsoft.Design", "CA1004:GenericMethodsShouldProvideTypeParameter", Justification = "This returns and verifies the appropriate message type.")]
		public TResponse Request<TResponse>(IDirectedProtocolMessage requestMessage)
			where TResponse : class, IProtocolMessage {
			IProtocolMessage response = this.Request(requestMessage);
			ErrorUtilities.VerifyProtocol(response != null, MessagingStrings.ExpectedMessageNotReceived, typeof(TResponse));

			var expectedResponse = response as TResponse;
			ErrorUtilities.VerifyProtocol(expectedResponse != null, MessagingStrings.UnexpectedMessageReceived, typeof(TResponse), response.GetType());

			return expectedResponse;
		}

		/// <summary>
		/// Sends a direct message to a remote party and waits for the response.
		/// </summary>
		/// <param name="requestMessage">The message to send.</param>
		/// <returns>The remote party's response.  Guaranteed to never be null.</returns>
		/// <exception cref="ProtocolException">Thrown if the response does not include a protocol message.</exception>
		public IProtocolMessage Request(IDirectedProtocolMessage requestMessage) {
			if (requestMessage == null) {
				throw new ArgumentNullException("requestMessage");
			}

			this.PrepareMessageForSending(requestMessage);
			Logger.DebugFormat("Sending request: {0}", requestMessage);
			var responseMessage = this.RequestInternal(requestMessage);
			ErrorUtilities.VerifyProtocol(responseMessage != null, MessagingStrings.ExpectedMessageNotReceived, typeof(IProtocolMessage).Name);

			Logger.DebugFormat("Received message response: {0}", responseMessage);
			this.VerifyMessageAfterReceiving(responseMessage);

			return responseMessage;
		}

		/// <summary>
		/// Gets the current HTTP request being processed.
		/// </summary>
		/// <returns>The HttpRequestInfo for the current request.</returns>
		/// <remarks>
		/// Requires an HttpContext.Current context.
		/// </remarks>
		/// <exception cref="InvalidOperationException">Thrown when <see cref="HttpContext.Current"/> is null.</exception>
		[SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate", Justification = "Costly call should not be a property.")]
		protected internal virtual HttpRequestInfo GetRequestFromContext() {
			if (HttpContext.Current == null) {
				throw new InvalidOperationException(MessagingStrings.HttpContextRequired);
			}

			return new HttpRequestInfo(HttpContext.Current.Request);
		}

		/// <summary>
		/// Fires the <see cref="Sending"/> event.
		/// </summary>
		/// <param name="message">The message about to be encoded and sent.</param>
		protected virtual void OnSending(IProtocolMessage message) {
			if (message == null) {
				throw new ArgumentNullException("message");
			}

			var sending = this.Sending;
			if (sending != null) {
				sending(this, new ChannelEventArgs(message));
			}
		}

		/// <summary>
		/// Submits a direct request message to some remote party and blocks waiting for an immediately reply.
		/// </summary>
		/// <param name="request">The request message.</param>
		/// <returns>The response message, or null if the response did not carry a message.</returns>
		/// <remarks>
		/// Typically a deriving channel will override <see cref="CreateHttpRequest"/> to customize this method's
		/// behavior.  However in non-HTTP frameworks, such as unit test mocks, it may be appropriate to override 
		/// this method to eliminate all use of an HTTP transport.
		/// </remarks>
		protected virtual IProtocolMessage RequestInternal(IDirectedProtocolMessage request) {
			HttpWebRequest webRequest = this.CreateHttpRequest(request);
			IDictionary<string, string> responseFields;

			using (DirectWebResponse response = this.WebRequestHandler.GetResponse(webRequest)) {
				if (response.ResponseStream == null) {
					return null;
				}

				responseFields = this.ReadFromResponseInternal(response);
			}

			IDirectResponseProtocolMessage responseMessage = this.MessageFactory.GetNewResponseMessage(request, responseFields);
			if (responseMessage == null) {
				return null;
			}

			var responseSerialize = MessageSerializer.Get(responseMessage.GetType());
			responseSerialize.Deserialize(responseFields, responseMessage);

			return responseMessage;
		}

		/// <summary>
		/// Gets the protocol message that may be embedded in the given HTTP request.
		/// </summary>
		/// <param name="request">The request to search for an embedded message.</param>
		/// <returns>The deserialized message, if one is found.  Null otherwise.</returns>
		protected virtual IDirectedProtocolMessage ReadFromRequestInternal(HttpRequestInfo request) {
			if (request == null) {
				throw new ArgumentNullException("request");
			}

			// Search Form data first, and if nothing is there search the QueryString
			var fields = request.Form.ToDictionary();
			if (fields.Count == 0) {
				fields = request.QueryString.ToDictionary();
			}

			return (IDirectedProtocolMessage)this.Receive(fields, request.GetRecipient());
		}

		/// <summary>
		/// Deserializes a dictionary of values into a message.
		/// </summary>
		/// <param name="fields">The dictionary of values that were read from an HTTP request or response.</param>
		/// <param name="recipient">Information about where the message was been directed.  Null for direct response messages.</param>
		/// <returns>The deserialized message, or null if no message could be recognized in the provided data.</returns>
		protected virtual IProtocolMessage Receive(Dictionary<string, string> fields, MessageReceivingEndpoint recipient) {
			if (fields == null) {
				throw new ArgumentNullException("fields");
			}

			IProtocolMessage message = this.MessageFactory.GetNewRequestMessage(recipient, fields);

			// If there was no data, or we couldn't recognize it as a message, abort.
			if (message == null) {
				return null;
			}

			// We have a message!  Assemble it.
			var serializer = MessageSerializer.Get(message.GetType());
			serializer.Deserialize(fields, message);

			return message;
		}

		/// <summary>
		/// Queues an indirect message for transmittal via the user agent.
		/// </summary>
		/// <param name="message">The message to send.</param>
		/// <returns>The pending user agent redirect based message to be sent as an HttpResponse.</returns>
		protected virtual UserAgentResponse SendIndirectMessage(IDirectedProtocolMessage message) {
			if (message == null) {
				throw new ArgumentNullException("message");
			}

			var serializer = MessageSerializer.Get(message.GetType());
			var fields = serializer.Serialize(message);
			UserAgentResponse response;
			if (CalculateSizeOfPayload(fields) > indirectMessageGetToPostThreshold) {
				response = this.CreateFormPostResponse(message, fields);
			} else {
				response = this.Create301RedirectResponse(message, fields);
			}

			return response;
		}

		/// <summary>
		/// Encodes an HTTP response that will instruct the user agent to forward a message to
		/// some remote third party using a 301 Redirect GET method.
		/// </summary>
		/// <param name="message">The message to forward.</param>
		/// <param name="fields">The pre-serialized fields from the message.</param>
		/// <returns>The encoded HTTP response.</returns>
		protected virtual UserAgentResponse Create301RedirectResponse(IDirectedProtocolMessage message, IDictionary<string, string> fields) {
			if (message == null) {
				throw new ArgumentNullException("message");
			}
			if (message.Recipient == null) {
				throw new ArgumentException(MessagingStrings.DirectedMessageMissingRecipient, "message");
			}
			if (fields == null) {
				throw new ArgumentNullException("fields");
			}

			WebHeaderCollection headers = new WebHeaderCollection();
			UriBuilder builder = new UriBuilder(message.Recipient);
			MessagingUtilities.AppendQueryArgs(builder, fields);
			headers.Add(HttpResponseHeader.Location, builder.Uri.AbsoluteUri);
			Logger.DebugFormat("Redirecting to {0}", builder.Uri.AbsoluteUri);
			UserAgentResponse response = new UserAgentResponse {
				Status = HttpStatusCode.Redirect,
				Headers = headers,
				Body = null,
				OriginalMessage = message
			};

			return response;
		}

		/// <summary>
		/// Encodes an HTTP response that will instruct the user agent to forward a message to
		/// some remote third party using a form POST method.
		/// </summary>
		/// <param name="message">The message to forward.</param>
		/// <param name="fields">The pre-serialized fields from the message.</param>
		/// <returns>The encoded HTTP response.</returns>
		protected virtual UserAgentResponse CreateFormPostResponse(IDirectedProtocolMessage message, IDictionary<string, string> fields) {
			if (message == null) {
				throw new ArgumentNullException("message");
			}
			if (message.Recipient == null) {
				throw new ArgumentException(MessagingStrings.DirectedMessageMissingRecipient, "message");
			}
			if (fields == null) {
				throw new ArgumentNullException("fields");
			}

			WebHeaderCollection headers = new WebHeaderCollection();
			StringWriter bodyWriter = new StringWriter(CultureInfo.InvariantCulture);
			StringBuilder hiddenFields = new StringBuilder();
			foreach (var field in fields) {
				hiddenFields.AppendFormat(
					"\t<input type=\"hidden\" name=\"{0}\" value=\"{1}\" />\r\n",
					HttpUtility.HtmlEncode(field.Key),
					HttpUtility.HtmlEncode(field.Value));
			}
			bodyWriter.WriteLine(
				indirectMessageFormPostFormat,
				HttpUtility.HtmlEncode(message.Recipient.AbsoluteUri),
				hiddenFields);
			bodyWriter.Flush();
			UserAgentResponse response = new UserAgentResponse {
				Status = HttpStatusCode.OK,
				Headers = headers,
				Body = bodyWriter.ToString(),
				OriginalMessage = message
			};

			return response;
		}

		/// <summary>
		/// Gets the protocol message that may be in the given HTTP response.
		/// </summary>
		/// <param name="response">The response that is anticipated to contain an protocol message.</param>
		/// <returns>The deserialized message parts, if found.  Null otherwise.</returns>
		protected abstract IDictionary<string, string> ReadFromResponseInternal(DirectWebResponse response);

		/// <summary>
		/// Prepares an HTTP request that carries a given message.
		/// </summary>
		/// <param name="request">The message to send.</param>
		/// <returns>The <see cref="HttpWebRequest"/> prepared to send the request.</returns>
		/// <remarks>
		/// This method must be overridden by a derived class, unless the <see cref="RequestInternal"/> method
		/// is overridden and does not require this method.
		/// </remarks>
		protected virtual HttpWebRequest CreateHttpRequest(IDirectedProtocolMessage request) {
			throw new NotImplementedException();
		}

		/// <summary>
		/// Queues a message for sending in the response stream where the fields
		/// are sent in the response stream in querystring style.
		/// </summary>
		/// <param name="response">The message to send as a response.</param>
		/// <returns>The pending user agent redirect based message to be sent as an HttpResponse.</returns>
		/// <remarks>
		/// This method implements spec V1.0 section 5.3.
		/// </remarks>
		protected abstract UserAgentResponse SendDirectMessageResponse(IProtocolMessage response);

		/// <summary>
		/// Prepares a message for transmit by applying signatures, nonces, etc.
		/// </summary>
		/// <param name="message">The message to prepare for sending.</param>
		/// <remarks>
		/// This method should NOT be called by derived types
		/// except when sending ONE WAY request messages.
		/// </remarks>
		protected void PrepareMessageForSending(IProtocolMessage message) {
			if (message == null) {
				throw new ArgumentNullException("message");
			}

			this.OnSending(message);

			MessageProtections appliedProtection = MessageProtections.None;
			foreach (IChannelBindingElement bindingElement in this.bindingElements) {
				if (bindingElement.PrepareMessageForSending(message)) {
					appliedProtection |= bindingElement.Protection;
				}
			}

			// Ensure that the message's protection requirements have been satisfied.
			if ((message.RequiredProtection & appliedProtection) != message.RequiredProtection) {
				throw new UnprotectedMessageException(message, appliedProtection);
			}

			EnsureValidMessageParts(message);
			message.EnsureValidMessage();
		}

		/// <summary>
		/// Prepares to send a request to the Service Provider as the query string in a GET request.
		/// </summary>
		/// <param name="requestMessage">The message to be transmitted to the ServiceProvider.</param>
		/// <returns>The web request ready to send.</returns>
		/// <remarks>
		/// This method is simply a standard HTTP Get request with the message parts serialized to the query string.
		/// This method satisfies OAuth 1.0 section 5.2, item #3.
		/// </remarks>
		protected virtual HttpWebRequest InitializeRequestAsGet(IDirectedProtocolMessage requestMessage) {
			if (requestMessage == null) {
				throw new ArgumentNullException("requestMessage");
			}

			var serializer = MessageSerializer.Get(requestMessage.GetType());
			var fields = serializer.Serialize(requestMessage);

			UriBuilder builder = new UriBuilder(requestMessage.Recipient);
			MessagingUtilities.AppendQueryArgs(builder, fields);
			HttpWebRequest httpRequest = (HttpWebRequest)WebRequest.Create(builder.Uri);

			return httpRequest;
		}

		/// <summary>
		/// Prepares to send a request to the Service Provider as the payload of a POST request.
		/// </summary>
		/// <param name="requestMessage">The message to be transmitted to the ServiceProvider.</param>
		/// <returns>The web request ready to send.</returns>
		/// <remarks>
		/// This method is simply a standard HTTP POST request with the message parts serialized to the POST entity
		/// with the application/x-www-form-urlencoded content type
		/// This method satisfies OAuth 1.0 section 5.2, item #2 and OpenID 2.0 section 4.1.2.
		/// </remarks>
		protected virtual HttpWebRequest InitializeRequestAsPost(IDirectedProtocolMessage requestMessage) {
			if (requestMessage == null) {
				throw new ArgumentNullException("requestMessage");
			}

			var serializer = MessageSerializer.Get(requestMessage.GetType());
			var fields = serializer.Serialize(requestMessage);

			HttpWebRequest httpRequest = (HttpWebRequest)WebRequest.Create(requestMessage.Recipient);
			httpRequest.Method = "POST";
			httpRequest.ContentType = "application/x-www-form-urlencoded";
			string requestBody = MessagingUtilities.CreateQueryString(fields);
			httpRequest.ContentLength = requestBody.Length;
			using (TextWriter writer = this.WebRequestHandler.GetRequestStream(httpRequest)) {
				writer.Write(requestBody);
			}

			return httpRequest;
		}

		/// <summary>
		/// Verifies the integrity and applicability of an incoming message.
		/// </summary>
		/// <param name="message">The message just received.</param>
		/// <exception cref="ProtocolException">
		/// Thrown when the message is somehow invalid.
		/// This can be due to tampering, replay attack or expiration, among other things.
		/// </exception>
		protected virtual void VerifyMessageAfterReceiving(IProtocolMessage message) {
			Debug.Assert(message != null, "message == null");

			MessageProtections appliedProtection = MessageProtections.None;
			foreach (IChannelBindingElement bindingElement in this.bindingElements.Reverse<IChannelBindingElement>()) {
				if (bindingElement.PrepareMessageForReceiving(message)) {
					appliedProtection |= bindingElement.Protection;
				}
			}

			// Ensure that the message's protection requirements have been satisfied.
			if ((message.RequiredProtection & appliedProtection) != message.RequiredProtection) {
				throw new UnprotectedMessageException(message, appliedProtection);
			}

			// We do NOT verify that all required message parts are present here... the 
			// message deserializer did for us.  It would be too late to do it here since
			// they might look initialized by the time we have an IProtocolMessage instance.
			message.EnsureValidMessage();
		}

		/// <summary>
		/// Verifies that all required message parts are initialized to values
		/// prior to sending the message to a remote party.
		/// </summary>
		/// <param name="message">The message to verify.</param>
		/// <exception cref="ProtocolException">
		/// Thrown when any required message part does not have a value.
		/// </exception>
		private static void EnsureValidMessageParts(IProtocolMessage message) {
			Debug.Assert(message != null, "message == null");

			MessageDictionary dictionary = new MessageDictionary(message);
			MessageDescription description = MessageDescription.Get(message.GetType(), message.Version);
			description.EnsureMessagePartsPassBasicValidation(dictionary);
		}

		/// <summary>
		/// Calculates a fairly accurate estimation on the size of a message that contains
		/// a given set of fields.
		/// </summary>
		/// <param name="fields">The fields that would be included in a message.</param>
		/// <returns>The size (in bytes) of the message payload.</returns>
		private static int CalculateSizeOfPayload(IDictionary<string, string> fields) {
			Debug.Assert(fields != null, "fields == null");

			int size = 0;
			foreach (var field in fields) {
				size += field.Key.Length;
				size += field.Value.Length;
				size += 2; // & and =
			}
			return size;
		}

		/// <summary>
		/// Ensures a consistent and secure set of binding elements and 
		/// sorts them as necessary for a valid sequence of operations.
		/// </summary>
		/// <param name="elements">The binding elements provided to the channel.</param>
		/// <returns>The properly ordered list of elements.</returns>
		/// <exception cref="ProtocolException">Thrown when the binding elements are incomplete or inconsistent with each other.</exception>
		private static IEnumerable<IChannelBindingElement> ValidateAndPrepareBindingElements(IEnumerable<IChannelBindingElement> elements) {
			if (elements == null) {
				return new IChannelBindingElement[0];
			}
			if (elements.Contains(null)) {
				throw new ArgumentException(MessagingStrings.SequenceContainsNullElement, "elements");
			}

			// Filter the elements between the mere transforming ones and the protection ones.
			var transformationElements = new List<IChannelBindingElement>(
				elements.Where(element => element.Protection == MessageProtections.None));
			var protectionElements = new List<IChannelBindingElement>(
				elements.Where(element => element.Protection != MessageProtections.None));

			bool wasLastProtectionPresent = true;
			foreach (MessageProtections protectionKind in Enum.GetValues(typeof(MessageProtections))) {
				if (protectionKind == MessageProtections.None) {
					continue;
				}

				int countProtectionsOfThisKind = protectionElements.Count(element => (element.Protection & protectionKind) == protectionKind);

				// Each protection binding element is backed by the presence of its dependent protection(s).
				if (countProtectionsOfThisKind > 0 && !wasLastProtectionPresent) {
					throw new ProtocolException(
						string.Format(
							CultureInfo.CurrentCulture,
							MessagingStrings.RequiredProtectionMissing,
							protectionKind));
				}

				// At most one binding element for each protection type.
				if (countProtectionsOfThisKind > 1) {
					throw new ProtocolException(
						string.Format(
							CultureInfo.CurrentCulture,
							MessagingStrings.TooManyBindingsOfferingSameProtection,
							protectionKind,
							countProtectionsOfThisKind));
				}
				wasLastProtectionPresent = countProtectionsOfThisKind > 0;
			}

			// Put the binding elements in order so they are correctly applied to outgoing messages.
			// Start with the transforming (non-protecting) binding elements first and preserve their original order.
			var orderedList = new List<IChannelBindingElement>(transformationElements);

			// Now sort the protection binding elements among themselves and add them to the list.
			orderedList.AddRange(protectionElements.OrderBy(element => element.Protection, BindingElementOutgoingMessageApplicationOrder));
			return orderedList;
		}

		/// <summary>
		/// Puts binding elements in their correct outgoing message processing order.
		/// </summary>
		/// <param name="protection1">The first protection type to compare.</param>
		/// <param name="protection2">The second protection type to compare.</param>
		/// <returns>
		/// -1 if <paramref name="element1"/> should be applied to an outgoing message before <paramref name="element2"/>.
		/// 1 if <paramref name="element2"/> should be applied to an outgoing message before <paramref name="element1"/>.
		/// 0 if it doesn't matter.
		/// </returns>
		private static int BindingElementOutgoingMessageApplicationOrder(MessageProtections protection1, MessageProtections protection2) {
			Debug.Assert(protection1 != MessageProtections.None || protection2 != MessageProtections.None, "This comparison function should only be used to compare protection binding elements.  Otherwise we change the order of user-defined message transformations.");

			// Now put the protection ones in the right order.
			return -((int)protection1).CompareTo((int)protection2); // descending flag ordinal order
		}
	}
}
