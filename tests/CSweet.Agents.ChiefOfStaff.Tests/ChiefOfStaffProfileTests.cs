using CSweet.Agents.ChiefOfStaff;

namespace CSweet.Agents.ChiefOfStaff.Tests;

public sealed class ChiefOfStaffProfileTests
{
    [Fact]
    public void Profile_UsesThirdPartyIdentityAndCompatibleConversationContract()
    {
        Assert.Equal("com.csweet.chief-of-staff", ChiefOfStaffProfile.AgentId);
        Assert.Equal("assistant.converse.v1", ChiefOfStaffProfile.ConverseCapability);
        Assert.Equal("com.csweet.user.message.received.v1", ChiefOfStaffProfile.UserMessageReceivedEvent);
        Assert.Equal("com.csweet.assistant.response.chunk.v1", ChiefOfStaffProfile.AssistantResponseChunkEvent);
    }
}
