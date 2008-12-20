﻿//-----------------------------------------------------------------------
// <copyright file="ExtensionBase.cs" company="Andrew Arnott">
//     Copyright (c) Andrew Arnott. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace DotNetOpenAuth.OpenId.Extensions {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using DotNetOpenAuth.OpenId.Messages;
	using DotNetOpenAuth.Messaging;

	public class ExtensionBase : IOpenIdProtocolMessageExtension {
		/// <summary>
		/// Backing store for the <see cref="IOpenIdProtocolMessageExtension.TypeUri"/> property.
		/// </summary>
		private string typeUri;

		/// <summary>
		/// Backing store for the <see cref="IOpenIdProtocolMessageExtension.AdditionalSupportedTypeUris"/> property.
		/// </summary>
		private IEnumerable<string> additionalSupportedTypeUris;

		/// <summary>
		/// Backing store for the <see cref="ExtraData"/> property.
		/// </summary>
		private Dictionary<string, string> extraData = new Dictionary<string, string>();

		/// <summary>
		/// Initializes a new instance of the <see cref="ExtensionBase"/> class.
		/// </summary>
		/// <param name="version">The version of the extension.</param>
		/// <param name="typeUri">The type URI to use in the OpenID message.</param>
		/// <param name="additionalSupportedTypeUris">The additional supported type URIs by which this extension might be recognized.</param>
		protected ExtensionBase(Version version, string typeUri, IEnumerable<string> additionalSupportedTypeUris) {
			this.Version = version;
			this.typeUri = typeUri;
			this.additionalSupportedTypeUris = additionalSupportedTypeUris;
		}

		#region IOpenIdProtocolMessageExtension Members

		/// <summary>
		/// Gets the TypeURI the extension uses in the OpenID protocol and in XRDS advertisements.
		/// </summary>
		string IOpenIdProtocolMessageExtension.TypeUri {
			get { return typeUri; }
		}

		/// <summary>
		/// Additional TypeURIs that are supported by this extension, in preferred order.
		/// May be empty if none other than <see cref="TypeUri"/> is supported, but
		/// should not be null.
		/// </summary>
		/// <value></value>
		/// <remarks>
		/// Useful for reading in messages with an older version of an extension.
		/// The value in the <see cref="TypeUri"/> property is always checked before
		/// trying this list.
		/// If you do support multiple versions of an extension using this method,
		/// consider adding a CreateResponse method to your request extension class
		/// so that the response can have the context it needs to remain compatible
		/// given the version of the extension in the request message.
		/// The <see cref="SimpleRegistration.ClaimsRequest.CreateResponse"/> for an example.
		/// </remarks>
		IEnumerable<string> IOpenIdProtocolMessageExtension.AdditionalSupportedTypeUris {
			get { return this.additionalSupportedTypeUris; }
		}

		#endregion

		#region IMessage Members

		/// <summary>
		/// Gets the version of the protocol or extension this message is prepared to implement.
		/// </summary>
		public Version Version { get; private set; }

		/// <summary>
		/// Gets the extra, non-standard Protocol parameters included in the message.
		/// </summary>
		/// <remarks>
		/// Implementations of this interface should ensure that this property never returns null.
		/// </remarks>
		IDictionary<string, string> IMessage.ExtraData {
			get { return this.extraData; }
		}

		/// <summary>
		/// Checks the message state for conformity to the protocol specification
		/// and throws an exception if the message is invalid.
		/// </summary>
		/// <remarks>
		/// 	<para>Some messages have required fields, or combinations of fields that must relate to each other
		/// in specialized ways.  After deserializing a message, this method checks the state of the
		/// message to see if it conforms to the protocol.</para>
		/// 	<para>Note that this property should <i>not</i> check signatures or perform any state checks
		/// outside this scope of this particular message.</para>
		/// </remarks>
		/// <exception cref="ProtocolException">Thrown if the message is invalid.</exception>
		void IMessage.EnsureValidMessage() {
			this.EnsureValidMessage();
		}

		#endregion

		/// <summary>
		/// Checks the message state for conformity to the protocol specification
		/// and throws an exception if the message is invalid.
		/// </summary>
		/// <remarks>
		/// 	<para>Some messages have required fields, or combinations of fields that must relate to each other
		/// in specialized ways.  After deserializing a message, this method checks the state of the
		/// message to see if it conforms to the protocol.</para>
		/// 	<para>Note that this property should <i>not</i> check signatures or perform any state checks
		/// outside this scope of this particular message.</para>
		/// </remarks>
		/// <exception cref="ProtocolException">Thrown if the message is invalid.</exception>
		protected virtual void EnsureValidMessage() {
		}
	}
}
