internal sealed class UsageMetadata
{
	public int PromptTokenCount { get; set; }
	public int ResponseTokenCount { get; set; }
	public int TotalTokenCount { get; set; }
	public TokenDetail[]? PromptTokensDetails { get; set; }
	public TokenDetail[]? ResponseTokensDetails { get; set; }
}