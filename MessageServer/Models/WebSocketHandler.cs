﻿using MessageServer.Data;
using System.Net.WebSockets;
using System.Reflection.Metadata;
using System.Text;
using Newtonsoft.Json;

namespace MessageServer.Models;

public class WebSocketHandler
{
	private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
	private readonly WebSocket [] sockets = new WebSocket [10];

	private RoomController _roomController = new RoomController();
	private UserController _userController = new UserController();

	DBManager dbManager = new DBManager("rpi4", "MessageServer", "App", "app");

	private bool logginEnabled = true;

	public void AddSocket(WebSocket socket)
	{
		// Find an available slot in the sockets array
		int index = Array.IndexOf(sockets, null);
		if (index >= 0) {
			sockets [index] = socket;
			StartHandling(socket, index);
		}
		else {
			// No available slots, close the socket
			socket.Abort();
		}
	}

	public async Task Stop()
	{
		// Stop handling WebSocket messages
		cancellation.Cancel();
		foreach (var socket in sockets) {
			if (socket != null) {
				await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server shutting down",
					CancellationToken.None);
			}
		}
	}

	private async Task StartHandling(WebSocket socket, int index)
	{
		// Handle WebSocket messages in a separate thread
		var buffer = new byte [16384];
		while (!cancellation.IsCancellationRequested) {

			try {

				var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

				if (result.MessageType == WebSocketMessageType.Close) {
					// Close the socket
					sockets [index] = null;
					Console.WriteLine("Client Disconnected:" + index);
					_userController.connectedClients.Remove(_userController.GetUserProfileFromSocketId(index));
					await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnected",
						CancellationToken.None);
				}
				else if (result.MessageType == WebSocketMessageType.Binary ||
						 result.MessageType == WebSocketMessageType.Text) {
					// Handle the message
					var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
					Console.WriteLine($"Received message from client {index}: {message}");

					ProcessMessage(index, message);
				}


			} catch (WebSocketException ex) {
				if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived) {
					await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnected", CancellationToken.None);
				}
				// Handle the client disconnection here
			//	sockets [index] = null;
			} catch (Exception ex) {
				Console.WriteLine($"Error receiving message: {ex.Message}");
				// Handle the error here
			}


		}
	}

	private void CommsToUser(string jsonUserData, string message)
	{
		Console.WriteLine(jsonUserData);
		User com = JsonConvert.DeserializeObject<User>(jsonUserData);
		foreach (var u in _userController.connectedClients) {
			if (u.GetUserName() == com.GetUserName()) {
				SendMessage(u.WebSocketID, $"RECIEVEMESSAGE:{jsonUserData}:{message}");
			}
		}
	}

	//TODO:validate message is not a server command / send with message i.e. "COMMSTOALLUSERS:USER:MESSAGE"
	private void CommsToAllButSender(int index, string message)
	{
		for (int i = 0; i < sockets.Length; i++) {
			if (sockets [i] != null && i != index) {
				SendMessage(index, message);
			}

		}
	}

	private void SendUsersOfRoom(int index, int roomID)
	{
		var msg = new StringBuilder();
		msg.Append("ROOMUSERLIST:");
		msg.Append(":" + _roomController.GetUsersInRoom(roomID).Count);
		if (_roomController.GetUsersInRoom(roomID).Count > 0) {
			foreach (var Usr in _roomController.GetUsersInRoom(roomID)) {
				msg.Append(":" + Usr.GetUserName());
			}
		}

		if (logginEnabled) {
			Console.WriteLine("Sending Room Users:" + msg.ToString());
		}

		SendMessage(index, msg.ToString());
	}

	private void SendRoomList(int index)
	{
		var msg = new StringBuilder();
		msg.Append("ROOMLIST");
		msg.Append(":" + _roomController.GetRoomList().Count);
		if (_roomController.GetRoomList().Count > 0) {
			foreach (var room in _roomController.GetRoomList()) {
				msg.Append($":{room.GetGuid()}");
			}
		}

		if (logginEnabled) {
			Console.ForegroundColor = ConsoleColor.Green;
			Console.WriteLine("Sending Room List:" + msg.ToString());
			Console.ResetColor();
		}
		SendMessage(index, msg.ToString());


	}

	private void SendRoomListJSON(int index)
	{
		SendMessage(index, "ROOMLIST*JSON:"+_roomController.JSONGetRoomList());
	}

	private async void GetUserList(int myIndex)
	{
		var returnMessage = new StringBuilder();

		returnMessage.Append("USERLIST:");
		returnMessage.Append(_userController.connectedClients.Count + ":");
		if (_userController.connectedClients.Count > 0) {
			foreach (var user in _userController.connectedClients) {
				returnMessage.Append(":" + user.GetUserName());
			}
		}

		Console.WriteLine("SENDING USER LIST@@ " + returnMessage.ToString());
		_ = Task.Run(() => SendMessage(myIndex, returnMessage.ToString()));

	}

	private bool SendMessage(int index, string message)
	{
		Console.WriteLine("Index:" + index +  "Socket State: " + sockets[index].State);
		// sockets[index].SendAsync();
		if (sockets [index] == null)
			return false;

		if (logginEnabled) {
			Console.ForegroundColor = ConsoleColor.Blue;
			Console.WriteLine(message);
			Console.ResetColor();
		}

		byte [] buffer = Encoding.UTF8.GetBytes(message);
		// Create a WebSocket message from the buffer
		var webSocketMessage = new ArraySegment<byte>(buffer);

		sockets [index].SendAsync(webSocketMessage, WebSocketMessageType.Text, true, CancellationToken.None);
		return true;
	}

	private void ProcessMessage(int index, string message)
	{
		string [] messageChunks = message.Split(':');

		switch (messageChunks [0]) {
			case "AUTHENTICATE":
			User? tmpUser = ValidateUser(message);
			if (tmpUser != null) {
				tmpUser.WebSocketID = index;

				bool Unique = true;

				foreach (var usr in _userController.connectedClients) {
					if (usr.WebSocketID == index)
						Unique = false;
				}

				if (Unique) {
					_userController.connectedClients.Add(tmpUser);
					Console.WriteLine("Added User to Client list:" + tmpUser.WebSocketID + "User:" + tmpUser.GetUserName());
				}
				else {
					Console.WriteLine("User Already Authenticated: " + tmpUser.WebSocketID + "User:" + tmpUser.GetUserName());
				}

				SendMessage(index, "AUTH:OK");
			}
			else // not authenticated
			{
				SendMessage(index, "AUTH:FAILED");

			}
			break;

			case "GETMYID":
			SendMessage(index, "IDIS:" + index);
			break;

			case "GETUSERLIST":
			GetUserList(index);
			break;

			//todo:sender should be sent with message for validation here or CommsToUser, also should have a return format i.e "SENDMESGTOUSER:USER:01:MESSAGE:Hello"
			case "SENDMESGTOUSER":
				string jsonStrUser = message.Substring(messageChunks[0].Length + 1, message.Length - (messageChunks [^1].Length + messageChunks [0].Length + 2));
			Console.WriteLine("Sending a Direct Message to:" + messageChunks [1]);
			CommsToUser(jsonStrUser, messageChunks [^1]);
			break;

			//TODO:sender should be sent with message for validation here or CommsToAllButSender, also should have a return format i.e "SENDMESGTOALL:USER:01:MESSAGE:Hello"
			case "SENDMESGTOALL":
			CommsToAllButSender(index, messageChunks [1]);
			break;

			case "CREATEROOM":
			int roomNumber = _roomController.CreateNewRoom(_userController.GetUserProfileFromSocketId(index), messageChunks);
			SendMessage(index, $"ROOMCREATED:{roomNumber}");
			SendMessage(index, "ROOMJOINED:" + roomNumber);
			break;

			case "ADDUSERTOROOM":
			var userProfile = _userController.GetUserProfileFromUserName(messageChunks [1]);
			_roomController.AddUserToRoom(userProfile, int.Parse(messageChunks [2]));
			SendMessage(index, "USERJOINED:" + messageChunks [1]);
			SendMessage(userProfile.WebSocketID, "ROOMJOINED:" + messageChunks [2]);
			break;
			
			case "SENDMSGTOROOM": //"SENDMSGTOROOM:[ROOMID]:[UserID]:[MESSAGE]"
				 
				foreach (var usr in _roomController.GetUsersInRoom(Int32.Parse(messageChunks[1])))
				{
					
						SendMessage(usr.WebSocketID, "ROOMMSG:" + messageChunks [1] + ":"+ messageChunks [2] +":"+ messageChunks[3]);
				}
				
				break;

			case "LISTUSERSINROOM":
			SendUsersOfRoom(index, int.Parse(messageChunks [1]));
			break;

			case "GETROOMLIST":
			SendRoomList(index);
			break;

			case "GETROOMLIST*JSON":
			SendRoomListJSON(index);	
			break;

			default:
			break;
		}
	}



	private User? ValidateUser(string message)
	{
		
		var messageChunks = message.Split(":");
		if (messageChunks.Length < 3)
			throw new Exception();

		if (dbManager.ValidateAccount(messageChunks [1], messageChunks [2])) {
			User? tmpUser = new User(messageChunks [1], true);
			return tmpUser;
		}

		return null;
	}
}