--邪恶★双子 强制断线
local cm,id=GetID()
function cm.initial_effect(c)
	--Activate
	local e1=Effect.CreateEffect(c)
	e1:SetDescription(aux.Stringid(id,0))
	e1:SetCategory(CATEGORY_NEGATE+CATEGORY_DRAW+CATEGORY_DESTROY)
	e1:SetType(EFFECT_TYPE_ACTIVATE)
	e1:SetCode(EVENT_CHAINING)
	e1:SetCountLimit(1,id+EFFECT_COUNT_CODE_OATH)
	e1:SetCondition(cm.condition)
	e1:SetTarget(cm.target)
	e1:SetOperation(cm.activate)
	c:RegisterEffect(e1)
end
--①：对方把怪兽的效果·魔法·陷阱卡发动时，那个发动无效
function cm.cfilter(c)
	return c:IsFaceup() and c:IsSetCard(0x2151) and c:IsType(TYPE_MONSTER)
end
function cm.condition(e,tp,eg,ep,ev,re,r,rp)
	if rp==tp then return false end
	if not Duel.IsChainNegatable(ev) then return false end
	if not (re:IsActiveType(TYPE_MONSTER) or re:IsHasType(EFFECT_TYPE_ACTIVATE)) then return false end
	return Duel.IsExistingMatchingCard(cm.cfilter,tp,LOCATION_MZONE,0,1,nil)
end
function cm.target(e,tp,eg,ep,ev,re,r,rp,chk)
	if chk==0 then return true end
	Duel.SetOperationInfo(0,CATEGORY_NEGATE,eg,1,0,0)
end
function cm.kfilter(c)
	return c:IsFaceup() and c:IsSetCard(0x152) and c:IsType(TYPE_MONSTER)
end
function cm.lfilter(c)
	return c:IsFaceup() and c:IsSetCard(0x153) and c:IsType(TYPE_MONSTER)
end
function cm.activate(e,tp,eg,ep,ev,re,r,rp)
	if not Duel.NegateActivation(ev) then return end
	local b1=Duel.IsExistingMatchingCard(cm.kfilter,tp,LOCATION_MZONE,0,1,nil)
	local b2=Duel.IsExistingMatchingCard(cm.lfilter,tp,LOCATION_MZONE,0,1,nil)
	if not b1 and not b2 then return end
	if not Duel.SelectYesNo(tp,aux.Stringid(id,1)) then return end
	if b1 then
		Duel.Draw(tp,1,REASON_EFFECT)
	end
	if b2 then
		Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_DESTROY)
		local g=Duel.SelectMatchingCard(tp,aux.TRUE,tp,LOCATION_ONFIELD,LOCATION_ONFIELD,1,1,nil)
		if g:GetCount()>0 then
			Duel.HintSelection(g)
			Duel.Destroy(g,REASON_EFFECT)
		end
	end
end
