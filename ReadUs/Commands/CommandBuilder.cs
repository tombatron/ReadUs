namespace ReadUs.Commands;

public static class CommandBuilder
{
    /// <summary>
    /// The name of the command you're invoking.
    ///
    /// For example, "LPUSH" is the `commandName` for the "left push" command that operates against a Redis list.
    /// </summary>
    /// <param name="command"></param>
    /// <returns>`CommandSpecification` that carries the current state of the builder.</returns>
    public static CommandSpecification WithCommand(string command) => new(commandName: command);
    
    /// <summary>
    /// The name of the sub-command you're invoking.
    ///
    /// For example, in the "CLUSTER NODES" command "NODES" is the sub-command.
    /// </summary>
    /// <param name="subCommand"></param>
    /// <returns>`CommandSpecification` that carries the current state of the builder.</returns>
    public static CommandSpecification WithSubCommand(string subCommand) => new(subCommandNames: [subCommand]);

    /// <summary>
    /// The sub-commands that you're invoking.
    ///
    /// For example, in the "CONFIG SET timeout 300" command "SET timeout 300" are the sub-commands.
    /// </summary>
    /// <param name="subCommands"></param>
    /// <returns>`CommandSpecification` that carries the current state of the builder.</returns>
    public static CommandSpecification WithSubCommands(params string[] subCommands) => new(subCommandNames: subCommands);

    /// <summary>
    /// The redis key that you're operating on.
    ///
    /// For example, in the "GET key-name" command "key-name" is the key.
    /// </summary>
    /// <param name="key"></param>
    /// <returns>`CommandSpecification` that carries the current state of the builder.</returns>
    public static CommandSpecification WithKey(RedisKey key) => new(redisKeys: [key]);

    /// <summary>
    /// The redis keys that you're operating on.
    ///
    /// For example, in the "MGET key-name1 key-name2" command "key-name1" and "key-name2" would be distinct key names.
    /// </summary>
    /// <param name="redisKeys"></param>
    /// <returns>`CommandSpecification` that carries the current state of the builder.</returns>
    public static CommandSpecification WithKeys(params RedisKey[] redisKeys) => new(redisKeys: redisKeys);

    /// <summary>
    /// An item is a single piece of data that we're passing to a Redis command.
    ///
    /// For example, in the "SET key-name key-value" command, "key-value" would be the data item. 
    /// </summary>
    /// <param name="item"></param>
    /// <returns>`CommandSpecification` that carries the current state of the builder.</returns>
    public static CommandSpecification AddItem(object? item) => new(items: [item]);

    /// <summary>
    /// A collection of item data that we're passing to a Redis command.
    ///
    /// For example, in the "LPUSH key-name item-1 item-2 item-3" command the items "item-1", "item-2", and "item-3" are the components of the item collection.
    /// </summary>
    /// <param name="items"></param>
    /// <returns>`CommandSpecification` that carries the current state of the builder.</returns>
    public static CommandSpecification AddItems(params object?[] items) => new(items: items);

    /// <summary>
    /// This specifies that the command will be encoded into a byte array using simple string encoding terminated by carriage return and line feed characters.
    /// </summary>
    /// <returns>`CommandSpecification` that carries the current state of the builder.</returns>
    public static CommandSpecification AsInlineCommand() => new(inlineCommand: true);
    
    /// <summary>
    /// This specifies that the command will be encoded into a byte array by encoding the command using RESP serialization. (This is the default)
    /// </summary>
    /// <returns>`CommandSpecification` that carries the current state of the builder.</returns>
    public static CommandSpecification AsEncodedCommand() => new(inlineCommand: false);

    public class CommandSpecification
    {
        private string? _commandName;
        private string[]? _subCommandNames;
        private RedisKey[]? _redisKeys;
        private List<object?> _items = [];
        private bool _inlineCommand;

        internal CommandSpecification(
            string? commandName = null, string[]? subCommandNames = null, RedisKey[]? redisKeys = null,
            object?[]? items = null, bool inlineCommand = false)
        {
            _commandName = commandName;
            _subCommandNames = subCommandNames;
            _redisKeys = redisKeys;
            _inlineCommand = inlineCommand;

            if (items is not null)
            {
                _items.AddRange(items);
            }
        }

        /// <summary>
        /// The name of the command you're invoking.
        ///
        /// For example, "LPUSH" is the `commandName` for the "left push" command that operates against a Redis list.
        /// </summary>
        /// <param name="commandName"></param>
        /// <returns>`CommandSpecification` that carries the current state of the builder.</returns>
        // ReSharper disable once MemberHidesStaticFromOuterClass
        public CommandSpecification WithCommand(string commandName)
        {
            _commandName = commandName;
            return this;
        }

        /// <summary>
        /// The name of the sub-command you're invoking.
        ///
        /// For example, in the "CLUSTER NODES" command "NODES" is the sub-command.
        /// </summary>
        /// <param name="subCommandName"></param>
        /// <returns>`CommandSpecification` that carries the current state of the builder.</returns>
        // ReSharper disable once MemberHidesStaticFromOuterClass
        public CommandSpecification WithSubCommand(string subCommandName)
        {
            _subCommandNames = [subCommandName];
            return this;
        }

        /// <summary>
        /// The sub-commands that you're invoking.
        ///
        /// For example, in the "CONFIG SET timeout 300" command "SET timeout 300" are the sub-commands.
        /// </summary>
        /// <param name="subCommandNames"></param>
        /// <returns>`CommandSpecification` that carries the current state of the builder.</returns>
        // ReSharper disable once MemberHidesStaticFromOuterClass
        public CommandSpecification WithSubCommands(params string[] subCommandNames)
        {
            _subCommandNames = subCommandNames;
            return this;
        }

        /// <summary>
        /// The redis key that you're operating on.
        ///
        /// For example, in the "GET key-name" command "key-name" is the key.
        /// </summary>
        /// <param name="key"></param>
        /// <returns>`CommandSpecification` that carries the current state of the builder.</returns>
        // ReSharper disable once MemberHidesStaticFromOuterClass
        public CommandSpecification WithKey(RedisKey key)
        {
            _redisKeys = [key];
            return this;
        }

        /// <summary>
        /// The redis keys that you're operating on.
        ///
        /// For example, in the "MGET key-name1 key-name2" command "key-name1" and "key-name2" would be distinct key names.
        /// </summary>
        /// <param name="redisKeys"></param>
        /// <returns>`CommandSpecification` that carries the current state of the builder.</returns>
        // ReSharper disable once MemberHidesStaticFromOuterClass
        public CommandSpecification WithKeys(params RedisKey[] redisKeys)
        {
            _redisKeys = redisKeys;
            return this;
        }

        /// <summary>
        /// An item is a single piece of data that we're passing to a Redis command.
        ///
        /// Calling this method multiple times will append the items. 
        ///
        /// For example, in the "SET key-name key-value" command, "key-value" would be the data item. 
        /// </summary>
        /// <param name="item"></param>
        /// <returns>`CommandSpecification` that carries the current state of the builder.</returns>
        // ReSharper disable once MemberHidesStaticFromOuterClass
        public CommandSpecification AddItem(object? item)
        {
            _items.Add(item);
            return this;
        }

        /// <summary>
        /// A collection of item data that we're passing to a Redis command.
        ///
        /// Calling this method multiple times will append. 
        ///
        /// For example, in the "LPUSH key-name item-1 item-2 item-3" command the items "item-1", "item-2", and "item-3" are the components of the item collection.
        /// </summary>
        /// <param name="items"></param>
        /// <returns>`CommandSpecification` that carries the current state of the builder.</returns>
        // ReSharper disable once MemberHidesStaticFromOuterClass
        public CommandSpecification AddItems(params object?[] items)
        {
            _items.AddRange(items);
            return this;
        }

        /// <summary>
        /// This specifies that the command will be encoded into a byte array using simple string encoding terminated by carriage return and line feed characters.
        /// </summary>
        /// <returns>`CommandSpecification` that carries the current state of the builder.</returns>
        // ReSharper disable once MemberHidesStaticFromOuterClass
        public CommandSpecification AsInlineCommand()
        {
            _inlineCommand = true;
            return this;
        }

        /// <summary>
        /// This specifies that the command will be encoded into a byte array by encoding the command using RESP serialization. (This is the default)
        /// </summary>
        /// <returns>`CommandSpecification` that carries the current state of the builder.</returns>
        // ReSharper disable once MemberHidesStaticFromOuterClass
        public CommandSpecification AsEncodedCommand()
        {
            _inlineCommand = false;
            return this;
        }

        /// <summary>
        /// Invoke the `Build` method in order to materialize the command that you've specified using the builder. 
        /// </summary>
        /// <returns>`RedisCommandEnvelope` instance that matches what you've configured using the builder.</returns>
        public RedisCommandEnvelope Build()
        {
            return new(command: _commandName, subCommands: _subCommandNames, keys: _redisKeys, simpleCommand: _inlineCommand, items: _items.ToArray());
        }
    }
}