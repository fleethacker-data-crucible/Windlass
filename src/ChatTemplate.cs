// Chat templates. C# 5 only. GGUF ships its template as Jinja; Windlass has no Jinja engine, so it recognises the
// template family and renders it in code, like llama.cpp's built-in templates (llm_chat_apply_template, MIT).
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Windlass
{
    public sealed class ChatMessage
    {
        public string Role, Content;
        public ChatMessage(string role, string content) { Role = role; Content = content ?? ""; }
    }

    public sealed class ChatTemplate
    {
        public static readonly string[] Names = { "llama3", "chatml", "mistral", "mistral-v7", "llama2" };

        public readonly string Name;
        readonly string _defaultSystem;
        readonly bool _stripThinking;

        ChatTemplate(string name, string defaultSystem, bool stripThinking)
        {
            Name = name; _defaultSystem = defaultSystem; _stripThinking = stripThinking;
        }

        // Picks the template from --chat-template, else from tokenizer.chat_template, else from the vocabulary.
        public static ChatTemplate Detect(GgufModelFile g, Tokenizer tok, string forced)
        {
            string jinja = g.GetString("tokenizer.chat_template", "");
            string name = forced;
            if (string.IsNullOrEmpty(name))
            {
                if (jinja.Contains("<|start_header_id|>")) name = "llama3";
                else if (jinja.Contains("<|im_start|>")) name = "chatml";
                else if (jinja.Contains("[SYSTEM_PROMPT]")) name = "mistral-v7";
                else if (jinja.Contains("<<SYS>>")) name = "llama2";
                else if (jinja.Contains("[INST]")) name = "mistral";
                else if (tok.HasToken("<|start_header_id|>")) name = "llama3";
                else if (tok.HasToken("<|im_start|>")) name = "chatml";
                else if (tok.HasToken("[INST]")) name = "mistral";
                else name = "chatml";
            }
            if (Array.IndexOf(Names, name) < 0) throw new ArgumentException("Unknown chat template '" + name + "'. Use one of: " + string.Join(", ", Names) + ".");
            string sys = null;
            Match m = Regex.Match(jinja, @"You are Qwen, created by Alibaba Cloud\. You are a helpful assistant\.");
            if (name == "chatml" && m.Success) sys = m.Value;
            return new ChatTemplate(name, sys, name == "chatml" && jinja.Contains("</think>"));
        }

        public static ChatTemplate Named(string name)
        {
            if (Array.IndexOf(Names, name) < 0) throw new ArgumentException("Unknown chat template '" + name + "'.");
            return new ChatTemplate(name, null, false);
        }

        // The prompt text, ending where the assistant's reply starts. BOS is added by the tokenizer, not here.
        public string Render(IList<ChatMessage> messages)
        {
            List<ChatMessage> msgs = new List<ChatMessage>();
            foreach (ChatMessage m in messages)
            {
                string role = m.Role == "developer" ? "system" : m.Role;
                string content = m.Content;
                if (_stripThinking && role == "assistant")
                {
                    int end = content.LastIndexOf("</think>", StringComparison.Ordinal);
                    if (end >= 0) content = content.Substring(end + 8).TrimStart('\n');
                }
                msgs.Add(new ChatMessage(role, content));
            }
            if (_defaultSystem != null && (msgs.Count == 0 || msgs[0].Role != "system")) msgs.Insert(0, new ChatMessage("system", _defaultSystem));

            StringBuilder sb = new StringBuilder();
            switch (Name)
            {
                case "llama3":
                    foreach (ChatMessage m in msgs)
                        sb.Append("<|start_header_id|>").Append(m.Role).Append("<|end_header_id|>\n\n").Append(m.Content.Trim()).Append("<|eot_id|>");
                    sb.Append("<|start_header_id|>assistant<|end_header_id|>\n\n");
                    break;
                case "chatml":
                    foreach (ChatMessage m in msgs)
                        sb.Append("<|im_start|>").Append(m.Role).Append('\n').Append(m.Content).Append("<|im_end|>\n");
                    sb.Append("<|im_start|>assistant\n");
                    break;
                case "mistral-v7":
                    foreach (ChatMessage m in msgs)
                    {
                        if (m.Role == "system") sb.Append("[SYSTEM_PROMPT]").Append(m.Content).Append("[/SYSTEM_PROMPT]");
                        else if (m.Role == "user") sb.Append("[INST]").Append(m.Content).Append("[/INST]");
                        else sb.Append(m.Content).Append("</s>");
                    }
                    break;
                case "mistral":
                case "llama2":
                {
                    // The system prompt goes in front of the first user turn.
                    string system = null;
                    bool llama2 = Name == "llama2";
                    foreach (ChatMessage m in msgs)
                    {
                        if (m.Role == "system") { system = system == null ? m.Content : system + "\n\n" + m.Content; continue; }
                        if (m.Role == "user")
                        {
                            string text = m.Content.Trim();
                            if (system != null)
                            {
                                text = llama2 ? "<<SYS>>\n" + system + "\n<</SYS>>\n\n" + text : system + "\n\n" + text;
                                system = null;
                            }
                            sb.Append("[INST] ").Append(text).Append(" [/INST]");
                        }
                        else sb.Append(' ').Append(m.Content.Trim()).Append("</s>");
                    }
                    break;
                }
            }
            return sb.ToString();
        }
    }
}
