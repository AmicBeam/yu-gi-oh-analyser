--邪恶★双子 夜幕宣言
local cm,id=GetID()
function cm.initial_effect(c)
	--link summon
	c:EnableReviveLimit()
	aux.AddLinkProcedure(c,nil,2,2,cm.lcheck)
	--set trap
	local e1=Effect.CreateEffect(c)
	e1:SetDescription(aux.Stringid(id,0))
	e1:SetCategory(CATEGORY_SSET)
	e1:SetType(EFFECT_TYPE_SINGLE+EFFECT_TYPE_TRIGGER_O)
	e1:SetCode(EVENT_SPSUMMON_SUCCESS)
	e1:SetProperty(EFFECT_FLAG_DELAY)
	e1:SetCountLimit(1,id)
	e1:SetTarget(cm.settg)
	e1:SetOperation(cm.setop)
	c:RegisterEffect(e1)
	--special summon
	local e2=Effect.CreateEffect(c)
	e2:SetDescription(aux.Stringid(id,1))
	e2:SetCategory(CATEGORY_SPECIAL_SUMMON)
	e2:SetType(EFFECT_TYPE_QUICK_O)
	e2:SetCode(EVENT_FREE_CHAIN)
	e2:SetRange(LOCATION_MZONE)
	e2:SetHintTiming(0,TIMINGS_CHECK_MONSTER+TIMING_END_PHASE)
	e2:SetCountLimit(1,id)
	e2:SetCost(cm.spcost)
	e2:SetTarget(cm.sptg)
	e2:SetOperation(cm.spop)
	c:RegisterEffect(e2)
end
--link material
function cm.lcheck(g,lc)
	return g:IsExists(cm.lfilter,1,nil)
end
function cm.lfilter(c)
	return c:IsLinkSetCard(0x152) or c:IsLinkSetCard(0x153)
end
--①：从卡组·墓地盖放任意数量的不同名「邪恶★双子」陷阱卡
function cm.setfilter(c)
	return c:IsSetCard(0x2151) and c:IsType(TYPE_TRAP) and c:IsSSetable()
end
function cm.settg(e,tp,eg,ep,ev,re,r,rp,chk)
	if chk==0 then
		return Duel.GetLocationCount(tp,LOCATION_SZONE)>0
			and Duel.IsExistingMatchingCard(aux.NecroValleyFilter(cm.setfilter),tp,LOCATION_DECK+LOCATION_GRAVE,0,1,nil)
	end
	Duel.SetOperationInfo(0,CATEGORY_SSET,nil,1,tp,LOCATION_DECK+LOCATION_GRAVE)
end
function cm.setop(e,tp,eg,ep,ev,re,r,rp)
	local slots=Duel.GetLocationCount(tp,LOCATION_SZONE)
	if slots<=0 then return end
	local candidates=Duel.GetMatchingGroup(aux.NecroValleyFilter(cm.setfilter),tp,LOCATION_DECK+LOCATION_GRAVE,0,nil)
	local selected=Group.CreateGroup()
	while selected:GetCount()<slots and candidates:GetCount()>0 do
		if selected:GetCount()>0 and not Duel.SelectYesNo(tp,aux.Stringid(id,2)) then break end
		Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_SET)
		local tc=candidates:Select(tp,1,1,nil):GetFirst()
		selected:AddCard(tc)
		candidates=candidates:Filter(aux.NOT(Card.IsCode),nil,tc:GetCode())
	end
	if selected:GetCount()>0 then
		Duel.SSet(tp,selected)
	end
end
--②：把这张卡解放，从额外卡组把1只暗属性·连接4以外的「邪恶★双子」怪兽特殊召唤
function cm.costfilter(c)
	return c:IsSetCard(0x2151) and c:IsAbleToRemoveAsCost()
end
function cm.spcost(e,tp,eg,ep,ev,re,r,rp,chk)
	local c=e:GetHandler()
	if chk==0 then
		return c:IsReleasable()
			and Duel.IsExistingMatchingCard(aux.NecroValleyFilter(cm.costfilter),tp,LOCATION_GRAVE,0,1,nil)
	end
	Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_REMOVE)
	local g=Duel.SelectMatchingCard(tp,aux.NecroValleyFilter(cm.costfilter),tp,LOCATION_GRAVE,0,1,1,nil)
	Duel.Remove(g,POS_FACEUP,REASON_COST)
	Duel.Release(c,REASON_COST)
end
function cm.spfilter(c,e,tp,rc)
	return c:IsSetCard(0x2151) and c:IsType(TYPE_MONSTER)
		and ( not c:IsAttribute(ATTRIBUTE_DARK) or c:GetLink()~=4)
		and c:IsCanBeSpecialSummoned(e,0,tp,false,false)
		and Duel.GetLocationCountFromEx(tp,tp,rc,c)>0
end
function cm.sptg(e,tp,eg,ep,ev,re,r,rp,chk)
	if chk==0 then
		return Duel.IsExistingMatchingCard(cm.spfilter,tp,LOCATION_EXTRA,0,1,nil,e,tp,e:GetHandler())
	end
	Duel.SetOperationInfo(0,CATEGORY_SPECIAL_SUMMON,nil,1,tp,LOCATION_EXTRA)
end
function cm.spop(e,tp,eg,ep,ev,re,r,rp)
	Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_SPSUMMON)
	local g=Duel.SelectMatchingCard(tp,cm.spfilter,tp,LOCATION_EXTRA,0,1,1,nil,e,tp,nil)
	if g:GetCount()>0 then
		Duel.SpecialSummon(g,0,tp,tp,false,false,POS_FACEUP)
	end
end
