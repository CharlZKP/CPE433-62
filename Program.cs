using System;
using System.Collections.Generic;
using System.Text;
using System.Net.Sockets;
using System.Net;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Configuration;


namespace DNWS
{
	// Main class
	public class Program
	{
		static public IConfigurationRoot Configuration { get; set; }

		// Log to console
		public void Log(String msg)
		{
			Console.WriteLine(msg);
		}

		// Start the server, Singleton here
		public void Start()
		{
			// Start server
			var builder = new ConfigurationBuilder();
			builder.SetBasePath(Directory.GetCurrentDirectory()).AddJsonFile("config.json");
			Configuration = builder.Build();
			DotNetWebServer ws = DotNetWebServer.GetInstance(this);
			ws.Start();
		}

		static void Main(string[] args)
		{
			Program p = new Program();
			p.Start();
		}
	}

	/// <summary>
	/// HTTP processor will process each http request
	/// </summary>

	public class HTTPProcessor
	{
		protected class PluginInfo
		{
			protected string _path;
			protected string _type;
			protected bool _preprocessing;
			protected bool _postprocessing;
			protected IPlugin _reference;

			public string path
			{
				get { return _path;}
				set {_path = value;}
			}
			public string type
			{
				get { return _type;}
				set {_type = value;}
			}
			public bool preprocessing
			{
				get { return _preprocessing;}
				set {_preprocessing = value;}
			}
			public bool postprocessing
			{
				get { return _postprocessing;}
				set {_postprocessing = value;}
			}
			public IPlugin reference
			{
				get { return _reference;}
				set {_reference = value;}
			}
		}
		// Get config from config manager, e.g., document root and port
		protected string ROOT = Program.Configuration["DocumentRoot"];

		protected string SITE_ROOT = Program.Configuration["DocumentRoot"] + "/www";

		protected Socket _client;
		protected Program _parent;
		protected Dictionary<string, PluginInfo> plugins;

		/// <summary>
		/// Constructor, set the client socket and parent ref, also init stat hash
		/// </summary>
		/// <param name="client">Client socket</param>
		/// <param name="parent">Parent ref</param>
		public HTTPProcessor(Socket client, Program parent)
		{
			_client = client;
			_parent = parent;
			plugins = new Dictionary<string, PluginInfo>();
			// load plugins
			var sections = Program.Configuration.GetSection("Plugins").GetChildren();
			foreach(ConfigurationSection section in sections) {
				PluginInfo pi = new PluginInfo();
				pi.path = section["Path"];
				pi.type = section["Class"];
				pi.preprocessing = section["Preprocessing"].ToLower().Equals("true");
				pi.postprocessing = section["Postprocessing"].ToLower().Equals("true");
				pi.reference = (IPlugin) Activator.CreateInstance(Type.GetType(pi.type));
				plugins[section["Path"]] = pi;
			}
		}

		/// <summary>
		/// Get a file from local harddisk based on path
		/// </summary>
		/// <param name="path">Absolute path to the file</param>
		/// <returns></returns>
		protected HTTPResponse getFile(String path)
		{
			HTTPResponse response = null;
			
			//Console.WriteLine("Client Requesting -> " + path);

			// Guess the content type from file extension
			string fileType = "text/html";
			if (path.ToLower().EndsWith("jpg") || path.ToLower().EndsWith("jpeg"))
			{
				fileType = "image/jpeg";
			}
			if (path.ToLower().EndsWith("png"))
			{
				fileType = "image/png";
			}

			// Try to read the file, if not found then 404, otherwise, 500.
			try
			{
				response = new HTTPResponse(200);
				response.type = fileType;
				response.body = System.IO.File.ReadAllBytes(path);
			}
			catch (FileNotFoundException ex)
			{
				response = new HTTPResponse(404);
				response.body = Encoding.UTF8.GetBytes("<h1>404 Not found</h1>");// + ex.Message
			}
			catch (Exception ex)
			{
				response = new HTTPResponse(500);
				response.body = Encoding.UTF8.GetBytes("<h1>500 Internal Server Error</h1>" + ex.Message);
			}
			return response;

		}

		// protected HTTPResponse error403() //later
		// {
		// 	HTTPResponse response = null;
		// 	response = new HTTPResponse(403);
		// 	String error_config = SITE_ROOT + "/error_conf.conf";
		// 	String path = System.IO.File.ReadAllText(error_config);
		// 	response.body = System.IO.File.ReadAllBytes(path);
		// 	return response;
		// }

		/// <summary>
		/// Get a request from client, process it, then return response to client
		/// </summary>
		public void Process()
		{
			NetworkStream ns = new NetworkStream(_client);
			string requestStr = "";
			HTTPRequest request = null;
			HTTPResponse response = null;
			byte[] bytes = new byte[1024];
			int bytesRead;

			// Read all request
			do
			{
				bytesRead = ns.Read(bytes, 0, bytes.Length);
				requestStr += Encoding.UTF8.GetString(bytes, 0, bytesRead);
			} while (ns.DataAvailable);

			request = new HTTPRequest(requestStr);
			request.addProperty("RemoteEndPoint", _client.RemoteEndPoint.ToString());
			
			// We can handle only GET now
			if(request.Status != 200) {
				response = new HTTPResponse(request.Status);
			}
			else
			{
				bool processed = false;
				// pre processing
				foreach(KeyValuePair<string, PluginInfo> plugininfo in plugins) {
					if(plugininfo.Value.preprocessing) {
						plugininfo.Value.reference.PreProcessing(request);
					}
				}
				// plugins
				foreach(KeyValuePair<string, PluginInfo> plugininfo in plugins) {
					if(request.Filename.StartsWith(plugininfo.Key)) {
						response = plugininfo.Value.reference.GetResponse(request);
						processed = true;
					}
				}
				// local file
				if(!processed) 
				{
					if (request.Filename.Equals("") || request.Filename.Equals("/"))
					{
						response = getFile(SITE_ROOT + "/index.html");
					}
					else
					{
						Boolean is_protected = false;

						if(File.Exists(SITE_ROOT + "/ignore_files.conf"))
						{
							//Console.WriteLine(SITE_ROOT + "/ignore_files.conf" + " Exist");
							String data_in_blocked_file = System.IO.File.ReadAllText(SITE_ROOT + "/ignore_files.conf");
							String[] protected_files = data_in_blocked_file.Split(
												new[] { "\r\n", "\r", "\n" },
    											StringSplitOptions.None
												);
							if(File.Exists(SITE_ROOT + "/" + request.Filename))
							{
								foreach(String file in protected_files)
								{
									//Console.WriteLine((file).ToLower() + " vs " + (request.Filename).ToLower());
									if((file).ToLower().Equals((request.Filename).ToLower()))
									{
										//Console.WriteLine("BLOCKED");
										is_protected = true;
										break;
									}
								}
							}
						}
						
						//Console.WriteLine("requesting: " + request.Filename);
						if(is_protected)
						{
							response = getFile(SITE_ROOT + "/index.html");
							//response = error403();
							//I'll replace with something like a 403 later
						}
						else
						{
							response = getFile(SITE_ROOT + "/" + request.Filename);
						}
						
					}
				}
				// post processing pipe
				foreach(KeyValuePair<string, PluginInfo> plugininfo in plugins) {
					if(plugininfo.Value.postprocessing) {
						response = plugininfo.Value.reference.PostProcessing(response);
					}
				}
			}
			// Generate response
			ns.Write(Encoding.UTF8.GetBytes(response.header), 0, response.header.Length);
			if(response.body != null) {
			  ns.Write(response.body, 0, response.body.Length);
			}

			// Shuting down
			//ns.Close();
			_client.Shutdown(SocketShutdown.Both);
			//_client.Close();

		}
	}

	public class TaskInfo
	{
		private HTTPProcessor _hp;
		public HTTPProcessor hp 
		{ 
			get {return _hp;}
			set {_hp = value;}
		}
		public TaskInfo(HTTPProcessor hp)
		{
			this.hp = hp;
		}
	}

	/// <summary>
	/// Main server class, open the socket and wait for client
	/// </summary>
	public class DotNetWebServer
	{
		protected int _port;
		protected Program _parent;
		protected Socket serverSocket;
		protected Socket clientSocket;
		private static DotNetWebServer _instance = null;
		protected int id;

		private DotNetWebServer(Program parent)
		{
			_parent = parent;
			id = 0;
		}

		/// <summary>
		/// Singleton here
		/// </summary>
		/// <param name="parent">parent ref</param>
		/// <returns></returns>
		public static DotNetWebServer GetInstance(Program parent)
		{
			if (_instance == null)
			{
				_instance = new DotNetWebServer(parent);
			}
			return _instance;
		}

		public void ThreadProc(Object stateinfo)
		{
			TaskInfo ti = stateinfo as TaskInfo;
			ti.hp.Process();
		}

		/// <summary>
		/// Server starting point
		/// </summary>
		public void Start()
		{
			_port = Convert.ToInt32(Program.Configuration["Port"]);
			IPEndPoint localEndPoint = new IPEndPoint(IPAddress.Any, _port);
			// Create listening socket, queue size is 5 now.
			serverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			serverSocket.Bind(localEndPoint);
			serverSocket.Listen(5);
			_parent.Log("Server started at port " + _port + ".");
			
			Boolean uses_thread_pool = Program.Configuration["uses_thread_pool"].ToLower().Equals("true");

			if(uses_thread_pool)
			{
				_parent.Log("Using thread pool");
			}
			

			while (true)
			{
				try
				{
				
					// Wait for client
					clientSocket = serverSocket.Accept();
					// Get one, show some info
					_parent.Log("Client accepted:" + clientSocket.RemoteEndPoint.ToString());
					HTTPProcessor hp = new HTTPProcessor(clientSocket, _parent);
					if(uses_thread_pool)
					{
						ThreadPool.QueueUserWorkItem(new WaitCallback(aFunction),processHP(hp));
					}
					else
					{
						hp.Process();
					}
				}
				catch (Exception ex)
				{
					_parent.Log("Server starting error: " + ex.Message + "\n" + ex.StackTrace);
				}
			}
		}

		static void aFunction(Object callback)
		{

		}

		public Boolean processHP(HTTPProcessor hp)
		{
			hp.Process();
			return true;
		}
	}
}
