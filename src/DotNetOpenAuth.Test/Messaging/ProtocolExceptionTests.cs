﻿//-----------------------------------------------------------------------
// <copyright file="ProtocolExceptionTests.cs" company="Andrew Arnott">
//     Copyright (c) Andrew Arnott. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace DotNetOpenAuth.Test.Messaging {
	using System;
	using DotNetOpenAuth.Messaging;
	using Microsoft.VisualStudio.TestTools.UnitTesting;

	[TestClass]
	public class ProtocolExceptionTests : TestBase {
		[TestMethod]
		public void CtorDefault() {
			ProtocolException ex = new ProtocolException();
		}

		[TestMethod]
		public void CtorWithTextMessage() {
			ProtocolException ex = new ProtocolException("message");
			Assert.AreEqual("message", ex.Message);
		}

		[TestMethod]
		public void CtorWithTextMessageAndInnerException() {
			Exception innerException = new Exception();
			ProtocolException ex = new ProtocolException("message", innerException);
			Assert.AreEqual("message", ex.Message);
			Assert.AreSame(innerException, ex.InnerException);
		}

		[TestMethod]
		public void CtorWithProtocolMessage() {
			IProtocolMessage request = new Mocks.TestDirectedMessage();
			Uri receiver = new Uri("http://receiver");
			ProtocolException ex = new ProtocolException("some error occurred", request, receiver);
			IDirectedProtocolMessage msg = (IDirectedProtocolMessage)ex;
			Assert.AreEqual("some error occurred", ex.Message);
			Assert.AreSame(receiver, msg.Recipient);
			Assert.AreEqual(request.Version, msg.Version);
			Assert.AreEqual(request.Transport, msg.Transport);
			msg.EnsureValidMessage();
		}

		[TestMethod, ExpectedException(typeof(ArgumentNullException))]
		public void CtorWithNullProtocolMessage() {
			new ProtocolException("message", null, new Uri("http://receiver"));
		}

		[TestMethod, ExpectedException(typeof(ArgumentNullException))]
		public void CtorWithNullReceiver() {
			new ProtocolException("message", new Mocks.TestDirectedMessage(MessageTransport.Indirect), null);
		}

		/// <summary>
		/// Tests that exceptions being sent as direct responses do not need an explicit receiver.
		/// </summary>
		[TestMethod]
		public void CtorUndirectedMessageWithNullReceiver() {
			IProtocolMessage request = new Mocks.TestDirectedMessage(MessageTransport.Direct);
			ProtocolException ex = new ProtocolException("message", request, null);
			IDirectedProtocolMessage msg = (IDirectedProtocolMessage)ex;
			Assert.IsNull(msg.Recipient);
			Assert.AreEqual(request.Version, msg.Version);
			Assert.AreEqual(request.Transport, msg.Transport);
		}

		[TestMethod, ExpectedException(typeof(InvalidOperationException))]
		public void ProtocolVersionWithoutMessage() {
			ProtocolException ex = new ProtocolException();
			IDirectedProtocolMessage msg = (IDirectedProtocolMessage)ex;
			var temp = msg.Version;
		}

		[TestMethod, ExpectedException(typeof(InvalidOperationException))]
		public void TransportWithoutMessage() {
			ProtocolException ex = new ProtocolException();
			IDirectedProtocolMessage msg = (IDirectedProtocolMessage)ex;
			var temp = msg.Transport;
		}

		[TestMethod, ExpectedException(typeof(InvalidOperationException))]
		public void RecipientWithoutMessage() {
			ProtocolException ex = new ProtocolException();
			IDirectedProtocolMessage msg = (IDirectedProtocolMessage)ex;
			var temp = msg.Recipient;
		}

		[TestMethod, ExpectedException(typeof(InvalidOperationException))]
		public void EnsureValidMessageWithoutMessage() {
			ProtocolException ex = new ProtocolException();
			IDirectedProtocolMessage msg = (IDirectedProtocolMessage)ex;
			msg.EnsureValidMessage();
		}
	}
}
