using Xunit;
using ReadUs.Commands;

namespace ReadUs.Tests.Commands;

public class CommandBuilderTests
{
    #region Static Factory Methods Tests

    [Fact]
    public void WithCommandName_SetsCommandName()
    {
        // Arrange & Act
        var result = CommandBuilder.WithCommandName("GET").Build();

        // Assert
        Assert.Equal("GET", result.Command);
    }

    [Fact]
    public void WithSubCommandName_SetsSingleSubCommand()
    {
        // Arrange & Act
        var result = CommandBuilder.WithSubCommandName("NODES").Build();

        // Assert
        Assert.NotNull(result.SubCommands);
        Assert.Single(result.SubCommands);
        Assert.Equal("NODES", result.SubCommands[0]);
    }

    [Fact]
    public void WithSubCommandNames_SetsMultipleSubCommands()
    {
        // Arrange & Act
        var result = CommandBuilder.WithSubCommandNames("SET", "timeout", "300").Build();

        // Assert
        Assert.NotNull(result.SubCommands);
        Assert.Equal(3, result.SubCommands.Length);
        Assert.Equal("SET", result.SubCommands[0]);
        Assert.Equal("timeout", result.SubCommands[1]);
        Assert.Equal("300", result.SubCommands[2]);
    }

    [Fact]
    public void WithKey_SetsSingleKey()
    {
        // Arrange
        var key = new RedisKey("mykey");

        // Act
        var result = CommandBuilder.WithKey(key).Build();

        // Assert
        Assert.NotNull(result.Keys);
        Assert.Single(result.Keys);
        Assert.Equal(key, result.Keys[0]);
    }

    [Fact]
    public void WithKeys_SetsMultipleKeys()
    {
        // Arrange
        var key1 = new RedisKey("key1");
        var key2 = new RedisKey("key2");
        var key3 = new RedisKey("key3");

        // Act
        var result = CommandBuilder.WithKeys(key1, key2, key3).Build();

        // Assert
        Assert.NotNull(result.Keys);
        Assert.Equal(3, result.Keys.Length);
        Assert.Equal(key1, result.Keys[0]);
        Assert.Equal(key2, result.Keys[1]);
        Assert.Equal(key3, result.Keys[2]);
    }

    [Fact]
    public void AddItem_SetsSingleItem()
    {
        // Arrange & Act
        var result = CommandBuilder.AddItem("value").Build();

        // Assert
        Assert.NotNull(result.Items);
        Assert.Single(result.Items);
        Assert.Equal("value", result.Items[0]);
    }

    [Fact]
    public void AddItem_WithNullItem_SetsNullItem()
    {
        // Arrange & Act
        var result = CommandBuilder.AddItem(null).Build();

        // Assert
        Assert.NotNull(result.Items);
        Assert.Single(result.Items);
        Assert.Null(result.Items[0]);
    }

    [Fact]
    public void AddItems_SetsMultipleItems()
    {
        // Arrange & Act
        var result = CommandBuilder.AddItems("item1", "item2", "item3").Build();

        // Assert
        Assert.NotNull(result.Items);
        Assert.Equal(3, result.Items.Length);
        Assert.Equal("item1", result.Items[0]);
        Assert.Equal("item2", result.Items[1]);
        Assert.Equal("item3", result.Items[2]);
    }

    [Fact]
    public void AddItems_WithMixedTypes_SetsItemsCorrectly()
    {
        // Arrange & Act
        var result = CommandBuilder.AddItems("string", 123, null, true).Build();

        // Assert
        Assert.NotNull(result.Items);
        Assert.Equal(4, result.Items.Length);
        Assert.Equal("string", result.Items[0]);
        Assert.Equal(123, result.Items[1]);
        Assert.Null(result.Items[2]);
        Assert.Equal(true, result.Items[3]);
    }

    [Fact]
    public void AsInlineCommand_SetsInlineCommandToTrue()
    {
        // Arrange & Act
        var result = CommandBuilder.AsInlineCommand().Build();

        // Assert
        Assert.True(result.SimpleCommand);
    }

    [Fact]
    public void AsEncodedCommand_SetsInlineCommandToFalse()
    {
        // Arrange & Act
        var result = CommandBuilder.AsEncodedCommand().Build();

        // Assert
        Assert.False(result.SimpleCommand);
    }

    #endregion

    #region Instance Method Tests

    [Fact]
    public void InstanceWithCommandName_SetsCommandName()
    {
        // Arrange & Act
        var result = CommandBuilder.WithCommandName("SET")
            .WithCommandName("GET")
            .Build();

        // Assert
        Assert.Equal("GET", result.Command);
    }

    [Fact]
    public void InstanceWithSubCommandName_SetsSingleSubCommand()
    {
        // Arrange & Act
        var result = CommandBuilder.WithCommandName("CLUSTER")
            .WithSubCommandName("NODES")
            .Build();

        // Assert
        Assert.NotNull(result.SubCommands);
        Assert.Single(result.SubCommands);
        Assert.Equal("NODES", result.SubCommands[0]);
    }

    [Fact]
    public void InstanceWithSubCommandNames_ReplacesExistingSubCommands()
    {
        // Arrange & Act
        var result = CommandBuilder.WithSubCommandName("OLD")
            .WithSubCommandNames("NEW1", "NEW2")
            .Build();

        // Assert
        Assert.NotNull(result.SubCommands);
        Assert.Equal(2, result.SubCommands.Length);
        Assert.Equal("NEW1", result.SubCommands[0]);
        Assert.Equal("NEW2", result.SubCommands[1]);
    }

    [Fact]
    public void InstanceWithKey_ReplacesExistingKey()
    {
        // Arrange
        var oldKey = new RedisKey("oldkey");
        var newKey = new RedisKey("newkey");

        // Act
        var result = CommandBuilder.WithKey(oldKey)
            .WithKey(newKey)
            .Build();

        // Assert
        Assert.NotNull(result.Keys);
        Assert.Single(result.Keys);
        Assert.Equal(newKey, result.Keys[0]);
    }

    [Fact]
    public void InstanceWithKeys_ReplacesExistingKeys()
    {
        // Arrange
        var key1 = new RedisKey("key1");
        var key2 = new RedisKey("key2");

        // Act
        var result = CommandBuilder.WithKey(key1)
            .WithKeys(key2, new RedisKey("key3"))
            .Build();

        // Assert
        Assert.NotNull(result.Keys);
        Assert.Equal(2, result.Keys.Length);
        Assert.Equal(key2, result.Keys[0]);
    }

    [Fact]
    public void InstanceAddItem_ReplacesExistingItems()
    {
        // Arrange & Act
        var result = CommandBuilder.AddItems("old1", "old2")
            .AddItem("new")
            .Build();

        // Assert
        Assert.NotNull(result.Items);
        Assert.Single(result.Items);
        Assert.Equal("new", result.Items[0]);
    }

    [Fact]
    public void InstanceAddItems_ReplacesExistingItems()
    {
        // Arrange & Act
        var result = CommandBuilder.AddItem("old")
            .AddItems("new1", "new2", "new3")
            .Build();

        // Assert
        Assert.NotNull(result.Items);
        Assert.Equal(3, result.Items.Length);
        Assert.Equal("new1", result.Items[0]);
        Assert.Equal("new2", result.Items[1]);
        Assert.Equal("new3", result.Items[2]);
    }

    [Fact]
    public void InstanceAsInlineCommand_SetsInlineCommandToTrue()
    {
        // Arrange & Act
        var result = CommandBuilder.WithCommandName("PING")
            .AsInlineCommand()
            .Build();

        // Assert
        Assert.True(result.SimpleCommand);
    }

    [Fact]
    public void InstanceAsEncodedCommand_SetsInlineCommandToFalse()
    {
        // Arrange & Act
        var result = CommandBuilder.WithCommandName("PING")
            .AsInlineCommand()
            .AsEncodedCommand()
            .Build();

        // Assert
        Assert.False(result.SimpleCommand);
    }

    #endregion

    #region Method Chaining Tests

    [Fact]
    public void ChainedMethods_BuildsCompleteCommand()
    {
        // Arrange
        var key = new RedisKey("mykey");

        // Act
        var result = CommandBuilder.WithCommandName("SET")
            .WithKey(key)
            .AddItem("myvalue")
            .Build();

        // Assert
        Assert.Equal("SET", result.Command);
        Assert.NotNull(result.Keys);
        Assert.Single(result.Keys);
        Assert.Equal(key, result.Keys[0]);
        Assert.NotNull(result.Items);
        Assert.Single(result.Items);
        Assert.Equal("myvalue", result.Items[0]);
    }

    [Fact]
    public void ChainedMethods_BuildsLPUSHCommand()
    {
        // Arrange
        var key = new RedisKey("mylist");

        // Act
        var result = CommandBuilder.WithCommandName("LPUSH")
            .WithKey(key)
            .AddItems("item1", "item2", "item3")
            .Build();

        // Assert
        Assert.Equal("LPUSH", result.Command);
        Assert.NotNull(result.Keys);
        Assert.Single(result.Keys);
        Assert.Equal(key, result.Keys[0]);
        Assert.NotNull(result.Items);
        Assert.Equal(3, result.Items.Length);
    }

    [Fact]
    public void ChainedMethods_BuildsMGETCommand()
    {
        // Arrange
        var key1 = new RedisKey("key1");
        var key2 = new RedisKey("key2");

        // Act
        var result = CommandBuilder.WithCommandName("MGET")
            .WithKeys(key1, key2)
            .Build();

        // Assert
        Assert.Equal("MGET", result.Command);
        Assert.NotNull(result.Keys);
        Assert.Equal(2, result.Keys.Length);
        Assert.Equal(key1, result.Keys[0]);
        Assert.Equal(key2, result.Keys[1]);
    }

    [Fact]
    public void ChainedMethods_BuildsCLUSTERNODESCommand()
    {
        // Arrange & Act
        var result = CommandBuilder.WithCommandName("CLUSTER")
            .WithSubCommandName("NODES")
            .Build();

        // Assert
        Assert.Equal("CLUSTER", result.Command);
        Assert.NotNull(result.SubCommands);
        Assert.Single(result.SubCommands);
        Assert.Equal("NODES", result.SubCommands[0]);
    }

    [Fact]
    public void ChainedMethods_BuildsCONFIGSETCommand()
    {
        // Arrange & Act
        var result = CommandBuilder.WithCommandName("CONFIG")
            .WithSubCommandNames("SET", "timeout", "300")
            .Build();

        // Assert
        Assert.Equal("CONFIG", result.Command);
        Assert.NotNull(result.SubCommands);
        Assert.Equal(3, result.SubCommands.Length);
        Assert.Equal("SET", result.SubCommands[0]);
        Assert.Equal("timeout", result.SubCommands[1]);
        Assert.Equal("300", result.SubCommands[2]);
    }

    [Fact]
    public void ChainedMethods_BuildsInlineCommand()
    {
        // Arrange & Act
        var result = CommandBuilder.WithCommandName("PING")
            .AsInlineCommand()
            .Build();

        // Assert
        Assert.Equal("PING", result.Command);
        Assert.True(result.SimpleCommand);
    }

    [Fact]
    public void ChainedMethods_BuildsComplexCommandWithAllComponents()
    {
        // Arrange
        var key = new RedisKey("complexkey");

        // Act
        var result = CommandBuilder.WithCommandName("ZADD")
            .WithKey(key)
            .AddItems(1.5, "member1", 2.3, "member2")
            .AsEncodedCommand()
            .Build();

        // Assert
        Assert.Equal("ZADD", result.Command);
        Assert.NotNull(result.Keys);
        Assert.Single(result.Keys);
        Assert.Equal(key, result.Keys[0]);
        Assert.NotNull(result.Items);
        Assert.Equal(4, result.Items.Length);
        Assert.False(result.SimpleCommand);
    }

    #endregion

    #region Default Values Tests

    [Fact]
    public void Build_WithNoParameters_CreatesCommandWithDefaults()
    {
        // Arrange & Act
        var result = CommandBuilder.WithCommandName("PING").Build();

        // Assert
        Assert.Equal("PING", result.Command);
        Assert.Null(result.SubCommands);
        Assert.Null(result.Keys);
        Assert.NotNull(result.Items);
        Assert.Empty(result.Items);
        Assert.False(result.SimpleCommand);
    }

    [Fact]
    public void Build_WithOnlySubCommands_CreatesCommandWithNullCommandName()
    {
        // Arrange & Act
        var result = CommandBuilder.WithSubCommandName("TEST").Build();

        // Assert
        Assert.Null(result.Command);
        Assert.NotNull(result.SubCommands);
        Assert.Single(result.SubCommands);
    }

    [Fact]
    public void Build_WithOnlyKey_CreatesCommandWithNullCommandName()
    {
        // Arrange & Act
        var result = CommandBuilder.WithKey(new RedisKey("key")).Build();

        // Assert
        Assert.Null(result.Command);
        Assert.NotNull(result.Keys);
        Assert.Single(result.Keys);
    }

    #endregion

    #region Edge Cases Tests

    [Fact]
    public void WithCommandName_WithEmptyString_SetsEmptyString()
    {
        // Arrange & Act
        var result = CommandBuilder.WithCommandName("").Build();

        // Assert
        Assert.Equal("", result.Command);
    }

    [Fact]
    public void WithSubCommandNames_WithEmptyArray_SetsEmptyArray()
    {
        // Arrange & Act
        var result = CommandBuilder.WithSubCommandNames().Build();

        // Assert
        Assert.NotNull(result.SubCommands);
        Assert.Empty(result.SubCommands);
    }

    [Fact]
    public void WithKeys_WithEmptyArray_SetsEmptyArray()
    {
        // Arrange & Act
        var result = CommandBuilder.WithKeys().Build();

        // Assert
        Assert.NotNull(result.Keys);
        Assert.Empty(result.Keys);
    }

    [Fact]
    public void AddItems_WithEmptyArray_SetsEmptyArray()
    {
        // Arrange & Act
        var result = CommandBuilder.AddItems().Build();

        // Assert
        Assert.NotNull(result.Items);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void MultipleBuilds_FromSameSpecification_ProducesIndependentResults()
    {
        // Arrange
        var spec = CommandBuilder.WithCommandName("GET");

        // Act
        var result1 = spec.Build();
        var result2 = spec.WithKey(new RedisKey("key1")).Build();

        // Assert
        Assert.Equal("GET", result1.Command);
        Assert.Null(result1.Keys);
        Assert.Equal("GET", result2.Command);
        Assert.NotNull(result2.Keys);
        Assert.Single(result2.Keys);
    }

    #endregion
}