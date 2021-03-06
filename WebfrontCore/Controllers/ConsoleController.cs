﻿using Microsoft.AspNetCore.Mvc;
using SharedLibraryCore;
using SharedLibraryCore.Database.Models;
using SharedLibraryCore.Dtos;
using SharedLibraryCore.Interfaces;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace WebfrontCore.Controllers
{
    public class ConsoleController : BaseController
    {
        public ConsoleController(IManager manager) : base(manager)
        {

        }

        public IActionResult Index()
        {
            var activeServers = Manager.GetServers().Select(s => new ServerInfo()
            {
                Name = s.Hostname,
                ID = s.EndPoint,
            });

            ViewBag.Description = "Use the IW4MAdmin web console to execute commands";
            ViewBag.Title = Localization["WEBFRONT_CONSOLE_TITLE"];
            ViewBag.Keywords = "IW4MAdmin, console, execute, commands";

            return View(activeServers);
        }

        public async Task<IActionResult> ExecuteAsync(long serverId, string command)
        {
            var server = Manager.GetServers().First(s => s.EndPoint == serverId);

            var client = new EFClient()
            {
                ClientId = Client.ClientId,
                Level = Client.Level,
                NetworkId = Client.NetworkId,
                CurrentServer = server,
                CurrentAlias = new EFAlias()
                {
                    Name = Client.Name
                }
            };

            var remoteEvent = new GameEvent()
            {
                Type = GameEvent.EventType.Command,
                Data = command,
                Origin = client,
                Owner = server,
                IsRemote = true
            };

            Manager.AddEvent(remoteEvent);
            CommandResponseInfo[] response = null;

            try
            {
                // wait for the event to process
                var completedEvent = await remoteEvent.WaitAsync(Utilities.DefaultCommandTimeout, server.Manager.CancellationToken);
         
                if (completedEvent.FailReason == GameEvent.EventFailReason.Timeout)
                {
                    response = new[]
                    {
                        new CommandResponseInfo()
                        {
                            ClientId = client.ClientId,
                            Response = Utilities.CurrentLocalization.LocalizationIndex["SERVER_ERROR_COMMAND_TIMEOUT"]
                        }
                    };
                }

                else
                {
                    response = response = server.CommandResult.Where(c => c.ClientId == client.ClientId).ToArray();
                }

                // remove the added command response
                for (int i = 0; i < response?.Length; i++)
                {
                    server.CommandResult.Remove(response[i]);
                }
            }

            catch (System.OperationCanceledException)
            {
                response = new[]
                {
                    new CommandResponseInfo()
                    {
                        ClientId = client.ClientId,
                        Response = Utilities.CurrentLocalization.LocalizationIndex["COMMADS_RESTART_SUCCESS"]
                    }
                };
            }

            return View("_Response", response);
        }
    }
}
