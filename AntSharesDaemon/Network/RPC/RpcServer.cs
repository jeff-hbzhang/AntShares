﻿using AntShares.Core;
using AntShares.IO;
using AntShares.IO.Json;
using AntShares.Wallets;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;

namespace AntShares.Network.RPC
{
    internal class RpcServer : IDisposable
    {
        private LocalNode localNode;
        private HttpListener listener = new HttpListener();
        private bool stopped = false;

        public RpcServer(LocalNode localNode)
        {
            this.localNode = localNode;
        }

        private static JObject CreateErrorResponse(JObject id, int code, string message, JObject data = null)
        {
            JObject response = CreateResponse(id);
            response["error"] = new JObject();
            response["error"]["code"] = code;
            response["error"]["message"] = message;
            if (data != null)
                response["error"]["data"] = data;
            return response;
        }

        private static JObject CreateResponse(JObject id)
        {
            JObject response = new JObject();
            response["jsonrpc"] = "2.0";
            response["id"] = id;
            return response;
        }

        public void Dispose()
        {
            if (listener.IsListening)
            {
                listener.Stop();
                while (!stopped) Thread.Sleep(100);
            }
            listener.Close();
        }

        private JObject InternalCall(string method, JArray _params)
        {
            switch (method)
            {
                case "getbalance":
                    if (Program.Wallet == null)
                        throw new RpcException(-400, "Access denied.");
                    else
                    {
                        UInt256 assetId = UInt256.Parse(_params[0].AsString());
                        IEnumerable<Coin> coins = Program.Wallet.FindCoins().Where(p => p.AssetId.Equals(assetId));
                        JObject json = new JObject();
                        json["balance"] = coins.Where(p => p.State == CoinState.Unspent || p.State == CoinState.Unconfirmed).Sum(p => p.Value).ToString();
                        json["confirmed"] = coins.Where(p => p.State == CoinState.Unspent).Sum(p => p.Value).ToString();
                        return json;
                    }
                case "getbestblockhash":
                    return Blockchain.Default.CurrentBlockHash.ToString();
                case "getblock":
                    {
                        Block block;
                        if (_params[0] is JNumber)
                        {
                            uint index = (uint)_params[0].AsNumber();
                            block = Blockchain.Default.GetBlock(index);
                        }
                        else
                        {
                            UInt256 hash = UInt256.Parse(_params[0].AsString());
                            block = Blockchain.Default.GetBlock(hash);
                        }
                        if (block == null)
                            throw new RpcException(-100, "Unknown block");
                        bool verbose = _params.Count >= 2 && _params[1].AsBooleanOrDefault(false);
                        if (verbose)
                        {
                            JObject json = block.ToJson();
                            json["confirmations"] = Blockchain.Default.Height - block.Height + 1;
                            UInt256 hash = Blockchain.Default.GetNextBlockHash(block.Hash);
                            if (hash != null)
                                json["nextblockhash"] = hash.ToString();
                            return json;
                        }
                        else
                        {
                            return block.ToArray().ToHexString();
                        }
                    }
                case "getblockcount":
                    return Blockchain.Default.Height + 1;
                case "getblockhash":
                    {
                        uint height = (uint)_params[0].AsNumber();
                        return Blockchain.Default.GetBlockHash(height).ToString();
                    }
                case "getconnectioncount":
                    return localNode.RemoteNodeCount;
                case "getrawmempool":
                    return new JArray(LocalNode.GetMemoryPool().Select(p => (JObject)p.Hash.ToString()));
                case "getrawtransaction":
                    {
                        UInt256 hash = UInt256.Parse(_params[0].AsString());
                        bool verbose = _params.Count >= 2 && _params[1].AsBooleanOrDefault(false);
                        int height = -1;
                        Transaction tx = LocalNode.GetTransaction(hash);
                        if (tx == null)
                            tx = Blockchain.Default.GetTransaction(hash, out height);
                        if (tx == null)
                            throw new RpcException(-101, "Unknown transaction");
                        if (verbose)
                        {
                            JObject json = tx.ToJson();
                            if (height >= 0)
                            {
                                Header header = Blockchain.Default.GetHeader((uint)height);
                                json["blockhash"] = header.Hash.ToString();
                                json["confirmations"] = Blockchain.Default.Height - header.Height + 1;
                                json["blocktime"] = header.Timestamp;
                            }
                            return json;
                        }
                        else
                        {
                            return tx.ToArray().ToHexString();
                        }
                    }
                case "gettxout":
                    {
                        UInt256 hash = UInt256.Parse(_params[0].AsString());
                        ushort index = (ushort)_params[1].AsNumber();
                        return Blockchain.Default.GetUnspent(hash, index)?.ToJson(index);
                    }
                case "sendrawtransaction":
                    {
                        Transaction tx = Transaction.DeserializeFrom(_params[0].AsString().HexToBytes());
                        return localNode.Relay(tx);
                    }
                case "sendtoaddress":
                    if (Program.Wallet == null)
                        throw new RpcException(-400, "Access denied");
                    else
                    {
                        UInt256 assetId = UInt256.Parse(_params[0].AsString());
                        UInt160 scriptHash = Wallet.ToScriptHash(_params[1].AsString());
                        Fixed8 value = Fixed8.Parse(_params[2].AsString());
                        Fixed8 fee = _params.Count >= 4 ? Fixed8.Parse(_params[3].AsString()) : Fixed8.Zero;
                        ContractTransaction tx = Program.Wallet.MakeTransaction(new ContractTransaction
                        {
                            Outputs = new[]
                            {
                                new TransactionOutput
                                {
                                    AssetId = assetId,
                                    Value = value,
                                    ScriptHash = scriptHash
                                }
                            }
                        }, fee);
                        if (tx == null)
                            throw new RpcException(-300, "Insufficient funds");
                        SignatureContext context = new SignatureContext(tx);
                        Program.Wallet.Sign(context);
                        if (context.Completed)
                        {
                            tx.Scripts = context.GetScripts();
                            Program.Wallet.SaveTransaction(tx);
                            localNode.Relay(tx);
                            return tx.ToJson();
                        }
                        else
                        {
                            return context.ToJson();
                        }
                    }
                case "submitblock":
                    {
                        Block block = _params[0].AsString().HexToBytes().AsSerializable<Block>();
                        return localNode.Relay(block);
                    }
                default:
                    throw new RpcException(-32601, "Method not found");
            }
        }

        private void Process(HttpListenerContext context)
        {
            try
            {
                context.Response.AddHeader("Access-Control-Allow-Origin", "*");
                context.Response.AddHeader("Access-Control-Allow-Methods", "POST");
                context.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type");
                context.Response.AddHeader("Access-Control-Max-Age", "31536000");
                if (context.Request.HttpMethod != "POST") return;
                JObject request = null;
                JObject response;
                using (StreamReader reader = new StreamReader(context.Request.InputStream))
                {
                    try
                    {
                        request = JObject.Parse(reader);
                    }
                    catch (FormatException) { }
                }
                if (request == null)
                {
                    response = CreateErrorResponse(null, -32700, "Parse error");
                }
                else if (request is JArray)
                {
                    JArray array = (JArray)request;
                    if (array.Count == 0)
                    {
                        response = CreateErrorResponse(request["id"], -32600, "Invalid Request");
                    }
                    else
                    {
                        response = array.Select(p => ProcessRequest(p)).Where(p => p != null).ToArray();
                    }
                }
                else
                {
                    response = ProcessRequest(request);
                }
                if (response == null || (response as JArray)?.Count == 0) return;
                context.Response.ContentType = "application/json-rpc";
                using (StreamWriter writer = new StreamWriter(context.Response.OutputStream))
                {
                    writer.Write(response.ToString());
                }
            }
            finally
            {
                context.Response.Close();
            }
        }

        private JObject ProcessRequest(JObject request)
        {
            if (!request.ContainsProperty("id")) return null;
            if (!request.ContainsProperty("method") || !request.ContainsProperty("params") || !(request["params"] is JArray))
            {
                return CreateErrorResponse(request["id"], -32600, "Invalid Request");
            }
            JObject result = null;
            try
            {
                result = InternalCall(request["method"].AsString(), (JArray)request["params"]);
            }
            catch (Exception ex)
            {
#if DEBUG
                return CreateErrorResponse(request["id"], ex.HResult, ex.Message, ex.StackTrace);
#else
                return CreateErrorResponse(request["id"], ex.HResult, ex.Message);
#endif
            }
            JObject response = CreateResponse(request["id"]);
            response["result"] = result;
            return response;
        }

        public async void Start(params string[] uriPrefix)
        {
            if (uriPrefix.Length == 0)
                throw new ArgumentException();
            foreach (string prefix in uriPrefix)
                listener.Prefixes.Add(prefix);
            listener.Start();
            while (listener.IsListening)
            {
                try
                {
                    Process(await listener.GetContextAsync());
                }
                catch (ApplicationException) { }
                catch (HttpListenerException) { }
            }
            stopped = true;
        }
    }
}
