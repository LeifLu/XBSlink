﻿/**
 * Project: XBSlink: A XBox360 & PS3/2 System Link Proxy
 * File name: xbs_udp_listener.cs
 *   
 * @author Oliver Seuffert, Copyright (C) 2011.
 */
/* 
 * XBSlink is free software; you can redistribute it and/or modify 
 * it under the terms of the GNU General Public License as published by 
 * the Free Software Foundation; either version 2 of the License, or 
 * (at your option) any later version.
 * 
 * This program is distributed in the hope that it will be useful, but 
 * WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY 
 * or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License 
 * for more details.
 * 
 * You should have received a copy of the GNU General Public License along 
 * with this program; If not, see <http://www.gnu.org/licenses/>
 */

using System;
using System.Collections.Generic;
using System.Text;
using System.Net.NetworkInformation;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using PacketDotNet;

namespace XBSlink
{
    class xbs_udp_message
    {
        public IPAddress src_ip;
        public int src_port;
        public xbs_node_message_type msg_type;
        public UInt16 data_len;
        public byte[] data;
    }

    class xbs_udp_listener_statistics
    {
        private static uint packets_in;
        private static uint packets_out;
        private static Object _locker = new Object();

        public static void increasePacketsIn()
        {
            lock (_locker)
                packets_in++;
        }

        public static void increasePacketsOut()
        {
            lock (_locker)
                packets_out++;
        }

        public static uint getPacketsIn()
        {
            uint count;
            lock (_locker)
                count = packets_in;
            return count;
        }

        public static uint getPacketsOut()
        {
            uint count;
            lock (_locker)
                count = packets_out;
            return count;
        }

    }

    class xbs_udp_listener
    {
        public const int standard_port = 31415;
        public int udp_socket_port;
        private Socket udp_socket = null;

        private IPEndPoint local_endpoint = null;
        private Thread dispatcher_thread = null;
        private Thread dispatcher_thread_out = null;
        private Thread receive_thread = null;
        private readonly Object _locker = new Object();
        private readonly Object _locker_out = new Object();
        private bool exiting = false;

        private Queue<xbs_node_message> out_msgs = new Queue<xbs_node_message>();
        private Queue<xbs_node_message> out_msgs_high_prio = new Queue<xbs_node_message>();
        private Queue<xbs_udp_message> in_msgs = new Queue<xbs_udp_message>();
        private Queue<xbs_udp_message> in_msgs_high_prio = new Queue<xbs_udp_message>();

        private xbs_node_list node_list = null;

        public readonly Object _locker_HELLO = new Object();

        public xbs_udp_listener()
        {
            initialize(IPAddress.Any, xbs_udp_listener.standard_port);
        }

        public xbs_udp_listener( IPAddress ip_endpoint, int port )
        {
            initialize(ip_endpoint, port);
        }

        private bool initialize(IPAddress ip_endpoint, int port)
        {
            node_list = FormMain.node_list;

            dispatcher_thread = new Thread(new ThreadStart(dispatcher));
            dispatcher_thread.IsBackground = true;
            dispatcher_thread.Priority = ThreadPriority.AboveNormal;
            dispatcher_thread.Start();
            dispatcher_thread_out = new Thread(new ThreadStart(dispatcher_out));
            dispatcher_thread_out.IsBackground = true;
            dispatcher_thread_out.Priority = ThreadPriority.AboveNormal;
            dispatcher_thread_out.Start();

            udp_socket_port = port;
            local_endpoint = new IPEndPoint(ip_endpoint, udp_socket_port);

            udp_socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                udp_socket.Bind(local_endpoint);
            }
            catch (SocketException)
            {
                FormMain.addMessage("!! Socket Exception: could not bind to port "+udp_socket_port);
                FormMain.addMessage("!! the UDP socket is not ready to send or receive packets.");
                FormMain.addMessage("!! please check if another application is running on this port.");
                System.Windows.Forms.MessageBox.Show("an error occured while initializing the UDP socket.\r\nPlease see the messages tab.");
                FormMain.abort_start_engine = true;
                return false;
            }
            udp_socket.ReceiveTimeout = 1000;
            receive_thread = new Thread( new ThreadStart(udp_receiver) );
            receive_thread.IsBackground = true;
            receive_thread.Priority = ThreadPriority.AboveNormal;
            receive_thread.Start();

            FormMain.addMessage(" * initialized udp listener on port " + port);
            return true;
        }

        public void shutdown()
        {
            exiting = true;
            lock (_locker)
                Monitor.PulseAll(_locker);
            lock (_locker_out)
                Monitor.PulseAll(_locker_out);
            if (dispatcher_thread.ThreadState != ThreadState.Stopped)
                dispatcher_thread.Join();
            if (dispatcher_thread_out.ThreadState != ThreadState.Stopped)
                dispatcher_thread_out.Join();
            udp_socket.Close();
        }

        public void udp_receiver()
        {
            FormMain.addMessage(" * udp receiver thread started");
            byte[] data = new byte[2048];
            byte[] data2 = new byte[2048];
            IPEndPoint remote_endpoint = new IPEndPoint(IPAddress.Any, 0);
            EndPoint ep = (EndPoint)remote_endpoint;
            int bytes = 0;
            xbs_udp_message msg = null;
#if !DEBUG
            try
            {
#endif
                while (!exiting)
                {
                    try
                    {
                        bytes = udp_socket.ReceiveFrom(data, ref ep);
                    }
                    catch (SocketException)
                    {
                        bytes = 0;
                    }
                    if (!exiting && bytes > 0)
                    {
                        xbs_node_message_type command = xbs_node_message.getMessageTypeFromUDPPacket(data);
                        msg = new xbs_udp_message();
                        msg.msg_type = command;
                        if (bytes > 3)
                        {
                            msg.data = new byte[bytes - 3];
                            Buffer.BlockCopy(data, 3, msg.data, 0, bytes - 3);
                            msg.data_len = (UInt16)(bytes - 3); // TODO: FIXME
                        }
                        else
                            msg.data_len = 0;
                        remote_endpoint = (IPEndPoint)ep;
                        msg.src_ip = remote_endpoint.Address;
                        msg.src_port = remote_endpoint.Port;

                        lock (_locker)
                        {
                            if (command == xbs_node_message_type.DATA)
                                lock (in_msgs_high_prio)
                                    in_msgs_high_prio.Enqueue(msg);
                            else
                                lock (in_msgs)
                                    in_msgs.Enqueue(msg);
                            Monitor.PulseAll(_locker);
                        }
                    }
                }
#if !DEBUG
            }
            catch (Exception ex)
            {
                ExceptionMessage.ShowExceptionDialog("udp_receiver service", ex);
            }
#endif
        }

        public void send_xbs_node_message(xbs_node_message msg)
        {
            if (node_list.local_node != null)
                if (msg.receiver.Equals(node_list.local_node))
                    return;
            lock (out_msgs)
                out_msgs.Enqueue(msg);
            lock (_locker_out)
                Monitor.PulseAll(_locker_out);
        }

        public void send_xbs_node_message_high_prio(xbs_node_message msg)
        {
            lock (out_msgs_high_prio)
                out_msgs_high_prio.Enqueue(msg);
            lock (_locker_out)
                Monitor.PulseAll(_locker_out);
        }

        public void dispatcher()
        {
            FormMain.addMessage(" * udp listener dispatcher thread starting...");
#if !DEBUG
            try
            {
#endif
                while (!exiting)
                {
                    lock (_locker)
                    {
                        Monitor.Wait(_locker);
                    }
                    if (!exiting)
                    {
                        dispatch_in_qeue();
                    }
                }
#if !DEBUG
            }
            catch (Exception ex)
            {
                ExceptionMessage.ShowExceptionDialog("udp dispatcher service", ex);
            }
#endif
        }

        public void dispatch_in_qeue()
        {
            xbs_udp_message udp_msg = null;
            int count_msgs = 0;
            int count_msgs_hp = 0;
            lock (in_msgs)
                count_msgs = in_msgs.Count;
            lock (in_msgs_high_prio)
                count_msgs_hp = in_msgs_high_prio.Count;

            while (count_msgs > 0 || count_msgs_hp > 0)
            {
                xbs_udp_listener_statistics.increasePacketsIn();

                if (count_msgs_hp > 0)
                {
                    lock (in_msgs_high_prio)
                        udp_msg = in_msgs_high_prio.Dequeue();
                    count_msgs_hp--;
                }
                else
                {
                    lock (in_msgs)
                        udp_msg = in_msgs.Dequeue();
                    count_msgs--;
                }
                dispatch_in_msg( ref udp_msg);

                // only recheck for new packets if all known packets are delivered
                if (count_msgs_hp == 0)
                {
                    lock (in_msgs_high_prio)
                        count_msgs_hp = in_msgs_high_prio.Count;
                    if (count_msgs == 0)
                        lock (in_msgs)
                            count_msgs = in_msgs.Count;
                }
            }
        }

        public void dispatch_in_msg(ref xbs_udp_message udp_msg)
        {
            xbs_node new_node = null;
# if DEBUG
            if (udp_msg.msg_type!=xbs_node_message_type.PING && udp_msg.msg_type!=xbs_node_message_type.PONG)
                FormMain.addMessage(" * IN " + udp_msg.msg_type + " " + udp_msg.src_ip+":"+udp_msg.src_port);
# endif
            switch (udp_msg.msg_type)
            {
                case xbs_node_message_type.DATA:
                    dispatch_DATA_message(ref udp_msg);
                    break;

                case xbs_node_message_type.ANNOUNCE:
                    new_node = new xbs_node(udp_msg.src_ip, udp_msg.src_port);
                    new_node.sendAddNodeMessage(node_list.local_node);
                    node_list.sendNodeListToNode(new_node);
                    break;

                case xbs_node_message_type.KNOWNNODE:
                    xbs_node_message_knownnode msg_knownnode = new xbs_node_message_knownnode(udp_msg.data);
                    new_node = node_list.findNode(msg_knownnode.ip, msg_knownnode.port);
                    if (new_node == null)
                    {
                        new_node = new xbs_node(msg_knownnode.ip, msg_knownnode.port);
#if DEBUG
                        FormMain.addMessage(" * trying to add known node: "+new_node);
#endif
                        node_list.tryAddingNode(new_node);
                    }
#if DEBUG
                    else
                        FormMain.addMessage(" * already in contact with node: " + new_node);
#endif
                    break;

                case xbs_node_message_type.ADDNODE:
                    xbs_node_message_addnode msg_addnode = new xbs_node_message_addnode(udp_msg.data);
# if DEBUG
                    FormMain.addMessage(" * received ADDNODE from " + udp_msg.src_ip + ":" + udp_msg.src_port + " for " + msg_addnode.ip + ":" + msg_addnode.port);
# endif
                    new_node = node_list.findNode(udp_msg.src_ip, udp_msg.src_port);
                    if (new_node == null)
                    {   // node not known, add to nodelist
                        new_node = node_list.addNode(msg_addnode.ip, msg_addnode.port, udp_msg.src_ip, udp_msg.src_port);
                        new_node.sendAddNodeMessage(node_list.local_node);
                    }
                    break;

                case xbs_node_message_type.DELNODE:
                    xbs_node_message_delnode msg_delnode = new xbs_node_message_delnode(udp_msg.data);
# if DEBUG  
                        FormMain.addMessage(" * received DELNODE from " + udp_msg.src_ip + ":" + udp_msg.src_port + " for " + msg_delnode.ip + ":" + msg_delnode.port);
# endif
                    try
                    {
                        new_node = node_list.delNode(udp_msg.src_ip, (UInt16)udp_msg.src_port);
                    }
                    catch (Exception ex)
                    {
                        FormMain.addMessage("!! error on deleting node: "+ex.Message);
                    }
                    if (new_node!=null)
                        xbs_chat.addSystemMessage(new_node.nickname + " left.");
                    break;

                case xbs_node_message_type.PING:
                    new_node = new xbs_node(udp_msg.src_ip, udp_msg.src_port);
                    xbs_node_message_pong msg_pong = new xbs_node_message_pong(new_node, udp_msg.data);
                    send_xbs_node_message(msg_pong);
                    break;

                case xbs_node_message_type.PONG:
                    new_node = node_list.findNode(udp_msg.src_ip, (UInt16)udp_msg.src_port);
                    if (new_node != null)
                        new_node.pong ( xbs_node_message_pong.getDelay(udp_msg.data).Milliseconds );
                    break;

                case xbs_node_message_type.GETNODELIST:
                    new_node = new xbs_node(udp_msg.src_ip, udp_msg.src_port);
                    node_list.sendNodeListToNode(new_node);
                    break;
                
                case xbs_node_message_type.GETCLIENTVERSION:
                    new_node = new xbs_node(udp_msg.src_ip, udp_msg.src_port);
                    xbs_node_message_clientversion msg_gcv = new xbs_node_message_clientversion(new_node, xbs_settings.xbslink_version);
                    send_xbs_node_message(msg_gcv);
                    break;

                case xbs_node_message_type.CLIENTVERSION:
                    new_node = node_list.findNode(udp_msg.src_ip, (UInt16)udp_msg.src_port);
                    if (new_node != null)
                    {
                        xbs_node_message_clientversion msg_cv = new xbs_node_message_clientversion(udp_msg.data);
                        new_node.client_version = msg_cv.version_string;
                    }
                    break;
                case xbs_node_message_type.CHATMSG:
                    new_node = node_list.findNode(udp_msg.src_ip, (UInt16)udp_msg.src_port);
                    if (new_node != null)
                    {
                        xbs_node_message_chatmsg msg_chat = new xbs_node_message_chatmsg(udp_msg.data);
                        xbs_chat.addChatMessage(new_node.nickname, msg_chat.getChatMessage());
                    }
                    break;
                case xbs_node_message_type.NICKNAME:
                    new_node = node_list.findNode(udp_msg.src_ip, (UInt16)udp_msg.src_port);
                    if (new_node != null)
                    {
                        xbs_node_message_nickname msg_nick = new xbs_node_message_nickname(udp_msg.data);
                        new_node.nickname = msg_nick.getNickname();
                        new_node.nickname_received = true;
                        xbs_chat.addSystemMessage(new_node.nickname + " joined.");
                    }
                    break;
                case xbs_node_message_type.GETNICKNAME:
                    new_node = new xbs_node(udp_msg.src_ip, udp_msg.src_port);
                    xbs_node_message_nickname msg_snick = new xbs_node_message_nickname(new_node, node_list.local_node.nickname);
                    send_xbs_node_message(msg_snick);
                    break;
                case xbs_node_message_type.SERVERHELLO:
                    lock (_locker_HELLO)
                    {
                        xbs_natstun.isPortReachable = true;
                        Monitor.PulseAll(_locker_HELLO);
                    }
                    break;
                case xbs_node_message_type.TO_CLOUDHELPER_HELPWITHNODE:
                    xbs_node_message_toCloudHelper_HelpWithNode msg_toCloudHelpWith = new xbs_node_message_toCloudHelper_HelpWithNode(udp_msg.data);
                    node_list.cloudhelper_helpWithNode(udp_msg.src_ip, udp_msg.src_port, msg_toCloudHelpWith.ip, msg_toCloudHelpWith.port);
                    break;
                case xbs_node_message_type.FROM_CLOUDHELPER_CONTACTNODE:
                    xbs_node_message_fromCloudHelper_ContactNode msg_fromCloudContactNode = new xbs_node_message_fromCloudHelper_ContactNode(udp_msg.data);
                    new_node = new xbs_node(msg_fromCloudContactNode.ip, msg_fromCloudContactNode.port);
                    new_node.sendAddNodeMessage(node_list.local_node);
                    break;
            }
        }

        private void dispatch_DATA_message(ref xbs_udp_message udp_msg)
        {
            byte[] src_mac = new byte[6];
            byte[] dst_mac = new byte[6];
            Buffer.BlockCopy(udp_msg.data, 0, dst_mac, 0, 6);
            PhysicalAddress dstMAC = new PhysicalAddress(dst_mac);
            Buffer.BlockCopy(udp_msg.data, 6, src_mac, 0, 6);
            PhysicalAddress srcMAC = new PhysicalAddress(src_mac);
#if DEBUG
            FormMain.addMessage(" * DATA ("+udp_msg.data.Length+") from "+srcMAC+" => "+dstMAC);
#endif
            FormMain.sniffer.injectRemotePacket(ref udp_msg.data, dstMAC, srcMAC);
            xbs_node node = node_list.findNode(udp_msg.src_ip, (UInt16)udp_msg.src_port);
            if (node != null)
                node.addXbox(srcMAC);
        }

        public void dispatcher_out()
        {
            FormMain.addMessage(" * udp outgoing dispatcher thread starting...");
#if !DEBUG
            try
            {
#endif
                while (!exiting)
                {
                    lock (_locker_out)
                    {
                        Monitor.Wait(_locker_out);
                    }
                    dispatch_out_queue();
                }
#if !DEBUG
            }
            catch (Exception ex)
            {
                ExceptionMessage.ShowExceptionDialog("udp dispatcher_out service", ex);
            }
#endif
        }

        public void dispatch_out_queue()
        {
            int count = 0, count_hp = 0;
            xbs_node_message msg = null;
            lock (out_msgs)
                count = out_msgs.Count;
            lock (out_msgs_high_prio)
                count_hp = out_msgs_high_prio.Count;
            while (count > 0 || count_hp > 0)
            {
                // High PRIO packets first (mostly DATA packets)
                if (count_hp>0)
                {
                    lock (out_msgs_high_prio)
                        msg = out_msgs_high_prio.Dequeue();
                    count_hp--;
                }
                else if (count > 0)
                {
                    lock (out_msgs)
                        msg = out_msgs.Dequeue();
                    count--;
                }

# if DEBUG
                if (msg.type != xbs_node_message_type.PING && msg.type != xbs_node_message_type.PONG)
                    FormMain.addMessage(" * OUT MSG " + msg.type + " " +msg.receiver);
# endif
                xbs_udp_listener_statistics.increasePacketsOut();
                byte[] bytes = msg.getByteArray();
                EndPoint ep = (EndPoint)new IPEndPoint(msg.receiver.getSendToIP(), msg.receiver.getSendToPort());
                try
                {
                    udp_socket.SendTo(bytes, bytes.Length, SocketFlags.None, ep);
                }
                catch (SocketException sock_ex)
                {
                    FormMain.addMessage("!! ERROR in dispatch_out_queue SendTo: "+sock_ex.Message);
                }
                
                // recheck for new packets
                if (count_hp == 0)
                {
                    lock (out_msgs_high_prio)
                        count_hp = out_msgs_high_prio.Count;
                    if (count == 0)
                        lock (out_msgs)
                            count = out_msgs.Count;
                }
            }
        }
    }
}